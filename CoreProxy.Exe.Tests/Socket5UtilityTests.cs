using CoreProxy.Common;
using CoreProxy.Exe.Tests.Infrastructure;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using System.Text;

namespace CoreProxy.Exe.Tests
{
    /// <summary>SOCKS5 请求解析：协议正确性直接决定目标地址对不对</summary>
    [TestClass]
    public sealed class Socket5UtilityTests
    {
        private static readonly TimeSpan Timeout = TimeSpan.FromSeconds(5);

        // ---------------- 地址类型 ----------------

        [TestMethod]
        public async Task Ipv4地址被正确解析()
        {
            var request = Request(0x01, [93, 184, 216, 34], port: 8080);

            var result = await ParseAsync(request);

            Assert.AreEqual("93.184.216.34", result.Address);
            Assert.AreEqual(8080, result.Port);
            Assert.AreEqual(0x01, result.Header.Atype);
        }

        /// <summary>端口必须按大端读：0x0102 大端=258，若误按小端会读成 513</summary>
        [TestMethod]
        public async Task 端口按大端解析()
        {
            Assert.AreEqual(258, (await ParseAsync(Request(0x01, [1, 2, 3, 4], port: 0x0102))).Port);
            Assert.AreEqual(8080, (await ParseAsync(Request(0x01, [1, 2, 3, 4], port: 0x1F90))).Port);
            Assert.AreEqual(65535, (await ParseAsync(Request(0x01, [1, 2, 3, 4], port: 0xFFFF))).Port);
        }

        [TestMethod]
        public async Task 域名被正确解析()
        {
            var result = await ParseAsync(Request(0x03, Encoding.UTF8.GetBytes("example.com"), port: 443));

            Assert.AreEqual("example.com", result.Address);
            Assert.AreEqual(443, result.Port);
            Assert.AreEqual("tcp://example.com:443", result.Uri);
        }

        /// <summary>ATYP=3 但内容是 IPv6 字面量时要补方括号，否则拼出来的 Uri 无法解析</summary>
        [TestMethod]
        public async Task 域名为IPv6字面量时补方括号()
        {
            var result = await ParseAsync(Request(0x03, Encoding.UTF8.GetBytes("::1"), port: 8080));

            Assert.AreEqual("[::1]", result.Address);
            Assert.AreEqual("tcp://[::1]:8080", result.Uri);
        }

        [TestMethod]
        public async Task IPv6地址被正确解析并补方括号()
        {
            var result = await ParseAsync(Request(0x04, [.. new byte[15], 1], port: 8080));

            Assert.AreEqual("[::1]", result.Address);
            Assert.AreEqual("tcp://[::1]:8080", result.Uri);
        }

        // ---------------- 非法请求 ----------------

        [TestMethod]
        public async Task 非socket5版本抛异常()
        {
            var request = Request(0x01, [1, 2, 3, 4], port: 80);
            request[0] = 0x04;

            await Assert.ThrowsExceptionAsync<Exception>(() => ParseAsync(request));
        }

        [TestMethod]
        public async Task 非CONNECT命令抛异常()
        {
            var request = Request(0x01, [1, 2, 3, 4], port: 80);
            request[1] = 0x02; //BIND

            await Assert.ThrowsExceptionAsync<Exception>(() => ParseAsync(request));
        }

        [TestMethod]
        public async Task RSV非0抛异常()
        {
            var request = Request(0x01, [1, 2, 3, 4], port: 80);
            request[2] = 0x01;

            await Assert.ThrowsExceptionAsync<Exception>(() => ParseAsync(request));
        }

        [TestMethod]
        public async Task 不支持的地址类型抛异常()
        {
            var request = Request(0x01, [1, 2, 3, 4], port: 80);
            request[3] = 0x02;

            await Assert.ThrowsExceptionAsync<Exception>(() => ParseAsync(request));
        }

        // ---------------- 真实网络形状 ----------------

        /// <summary>真实连接里字节是分片到达的，逐字节喂也必须解析正确</summary>
        [TestMethod]
        public async Task 逐字节分片到达仍能解析()
        {
            var request = Request(0x03, Encoding.UTF8.GetBytes("www.example.org"), port: 8080);
            var pipe = new System.IO.Pipelines.Pipe();
            using var cts = new CancellationTokenSource(Timeout);

            var parseTask = Socket5Utility.Parse(pipe.Reader, cts.Token);

            await TestPipeline.WriteFragmentedAsync(pipe.Writer, request, cts.Token);

            var result = await parseTask.WaitAsync(Timeout);
            Assert.AreEqual("www.example.org", result.Address);
            Assert.AreEqual(8080, result.Port);
        }

        /// <summary>解析必须只吃掉 SOCKS5 请求的字节，紧随其后的负载要原样留在管道里（否则就是数据丢失）</summary>
        [TestMethod]
        public async Task 解析后管道中的负载数据保持完整()
        {
            var payload = Encoding.ASCII.GetBytes("GET / HTTP/1.1\r\nHost: example.com\r\n\r\n");
            var request = Request(0x03, Encoding.UTF8.GetBytes("example.com"), port: 80);
            var pipe = TestPipeline.FromBytes(request, payload);
            using var cts = new CancellationTokenSource(Timeout);

            var result = await Socket5Utility.Parse(pipe.Reader, cts.Token);

            Assert.AreEqual("example.com", result.Address);
            AssertBytesEqual(payload, await TestPipeline.ReadExactlyAsync(pipe.Reader, payload.Length, cts.Token));
        }

        /// <summary>
        /// 记录当前行为：RFC 1928 要求域名长度至少为 1，但这里长度为 0 时得到空地址而不是报错。
        /// 这条用例只锁定现状，不代表这是期望行为
        /// </summary>
        [TestMethod]
        public async Task 域名长度为0时得到空地址_记录现状()
        {
            var result = await ParseAsync(Request(0x03, [], port: 80));

            Assert.AreEqual(string.Empty, result.Address);
        }

        // ---------------- 辅助 ----------------

        private static async Task<Socket5Result> ParseAsync(byte[] request)
        {
            var pipe = TestPipeline.FromBytes(request);
            using var cts = new CancellationTokenSource(Timeout);
            return await Socket5Utility.Parse(pipe.Reader, cts.Token).WaitAsync(Timeout);
        }

        /// <summary>VER=5 CONNECT 请求</summary>
        private static byte[] Request(byte atyp, byte[] address, ushort port)
        {
            var bytes = new List<byte> { 0x05, 0x01, 0x00, atyp };
            if (atyp == 0x03)
            {
                bytes.Add((byte)address.Length);
            }

            bytes.AddRange(address);
            bytes.Add((byte)(port >> 8));   //大端
            bytes.Add((byte)(port & 0xFF));
            return [.. bytes];
        }

        private static void AssertBytesEqual(byte[] expected, byte[] actual)
        {
            Assert.AreEqual(expected.Length, actual.Length, "字节数不一致");
            for (var i = 0; i < expected.Length; i++)
            {
                if (expected[i] != actual[i])
                {
                    Assert.Fail($"第 {i} 字节不一致：期望 0x{expected[i]:X2}，实际 0x{actual[i]:X2}");
                }
            }
        }
    }
}
