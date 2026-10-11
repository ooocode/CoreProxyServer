using Google.Protobuf;
using Hello;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using System.Threading.Channels;

namespace CoreProxy.Exe.Tests.Infrastructure
{
    public static class ClientTestHelpers
    {
        /// <summary>SOCKS5 方法选择应答：05 00</summary>
        public static readonly byte[] MethodSelectOk = [0x05, 0x00];

        /// <summary>SOCKS5 CONNECT 成功应答（固定回 127.0.0.1:8000）</summary>
        public static readonly byte[] ConnectOk = [0x05, 0x00, 0x00, 0x01, 0x7f, 0x00, 0x00, 0x01, 0x1f, 0x40];

        /// <summary>HTTP CONNECT 成功应答</summary>
        public static byte[] HttpConnectOk => "HTTP/1.1 200 Connection Established\r\n\r\n"u8.ToArray();

        public static byte[] RandomBytes(int length, int seed)
        {
            var buffer = new byte[length];
            new Random(seed).NextBytes(buffer);
            return buffer;
        }

        /// <summary>按随机分片切分，覆盖 1 字节等边界</summary>
        public static IEnumerable<byte[]> Chunked(byte[] data, int seed)
        {
            var random = new Random(seed);
            var offset = 0;
            while (offset < data.Length)
            {
                var size = Math.Min(random.Next(1, 128 * 1024), data.Length - offset);
                var chunk = new byte[size];
                Array.Copy(data, offset, chunk, 0, size);
                offset += size;
                yield return chunk;
            }
        }

        /// <summary>把客户端写往服务器的多条消息拼成字节流</summary>
        public static async Task<byte[]> ReadMessagesAsync(ChannelReader<HttpData> reader, int expectedBytes, CancellationToken cancellationToken)
        {
            using var stream = new MemoryStream(expectedBytes);
            while (stream.Length < expectedBytes)
            {
                var message = await reader.ReadAsync(cancellationToken);
                stream.Write(message.Payload.Span);
            }

            return stream.ToArray();
        }

        public static void AssertBytesEqual(byte[] expected, byte[] actual, string? message = null)
        {
            Assert.AreEqual(expected.Length, actual.Length, $"{message} 字节数不一致".Trim());
            for (var i = 0; i < expected.Length; i++)
            {
                if (expected[i] != actual[i])
                {
                    Assert.Fail($"{message} 第 {i} 字节不一致：期望 0x{expected[i]:X2}，实际 0x{actual[i]:X2}".Trim());
                }
            }
        }

        public static async Task WaitUntilAsync(Func<bool> condition, TimeSpan timeout)
        {
            var deadline = DateTime.UtcNow + timeout;
            while (DateTime.UtcNow < deadline)
            {
                if (condition())
                {
                    return;
                }

                await Task.Delay(10);
            }

            Assert.Fail("等待条件超时");
        }

        /// <summary>VER=5 NMETHODS=1 METHOD=0 的握手</summary>
        public static byte[] Greeting() => [0x05, 0x01, 0x00];

        /// <summary>CONNECT 请求（域名形式）</summary>
        public static byte[] ConnectRequest(string host, ushort port)
        {
            var hostBytes = System.Text.Encoding.UTF8.GetBytes(host);
            var bytes = new List<byte> { 0x05, 0x01, 0x00, 0x03, (byte)hostBytes.Length };
            bytes.AddRange(hostBytes);
            bytes.Add((byte)(port >> 8));
            bytes.Add((byte)(port & 0xFF));
            return [.. bytes];
        }
    }
}
