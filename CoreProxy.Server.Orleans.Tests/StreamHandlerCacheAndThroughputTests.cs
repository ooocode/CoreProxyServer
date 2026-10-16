using CoreProxy.Server.Orleans.Internal;
using CoreProxy.Server.Orleans.Tests.Infrastructure;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using System.Diagnostics;

namespace CoreProxy.Server.Orleans.Tests
{
    /// <summary>
    /// 通过真实 StreamHandler（走真实 socket 目标连接）验证 DNS 缓存带来的建连优化，
    /// 以及数据面吞吐没有回归。
    /// </summary>
    [TestClass]
    public sealed class StreamHandlerCacheAndThroughputTests
    {
        public TestContext TestContext { get; set; } = null!;

        private static readonly TimeSpan Timeout = TimeSpan.FromSeconds(30);

        private const int MiB = 1024 * 1024;

        // ---------------- 建连路径：DNS 缓存实际生效 ----------------

        /// <summary>
        /// 多条隧道指向同一主机名，共用一个解析器实例（即生产里 DI 的单例）。
        /// 走真实 DnsResolver（114）解析 localhost → 127.0.0.1；只有第一次建连 miss，其余全部命中缓存。
        /// </summary>
        [TestMethod]
        public async Task RepeatedTunnelsToSameHostResolveDnsOnlyOnce()
        {
            using var server = new TestTargetServer();
            var cache = new MemoryCache(new MemoryCacheOptions { TrackStatistics = true });
            var resolver = new TargetResolver(cache, TimeSpan.FromHours(1));

            for (var i = 0; i < 3; i++)
            {
                await using var tunnel = Tunnel.Start($"http://localhost:{server.Port}/", resolver: resolver);
                var target = await server.AcceptAsync(Timeout); //真实连接建立成功，证明解析结果落到了监听地址
                await tunnel.ReadHandshakeAsync(Timeout);

                tunnel.CompleteClientStream();
                target.Dispose(); //目标侧关闭，让隧道收尾（半关闭后服务端方向仍开着，必须显式关）
                await tunnel.Task.WaitAsync(Timeout);
            }

            var stats = cache.GetCurrentStatistics()!;
            Assert.AreEqual(1, stats.TotalMisses, "共享解析器下，3 条隧道只应发起 1 次 DNS 解析（其余命中缓存）");
            Assert.AreEqual(1, stats.CurrentEntryCount);
        }

        /// <summary>TTL=0 关闭缓存：每条隧道重新解析，且不写入缓存</summary>
        [TestMethod]
        public async Task DisabledCacheResolvesEveryTunnel()
        {
            using var server = new TestTargetServer();
            var cache = new MemoryCache(new MemoryCacheOptions { TrackStatistics = true });
            var resolver = new TargetResolver(cache, TimeSpan.Zero);

            for (var i = 0; i < 3; i++)
            {
                await using var tunnel = Tunnel.Start($"http://localhost:{server.Port}/", resolver: resolver);
                var target = await server.AcceptAsync(Timeout);
                await tunnel.ReadHandshakeAsync(Timeout);

                tunnel.CompleteClientStream();
                target.Dispose();
                await tunnel.Task.WaitAsync(Timeout);
            }

            Assert.AreEqual(0, cache.GetCurrentStatistics()!.CurrentEntryCount, "TTL=0 时不应写入缓存");
        }

        // ---------------- 数据面：吞吐回归 ----------------

        /// <summary>
        /// 目标 -> 客户端方向的吞吐回归。界放得很宽：目标是捕捉量级级的退化
        /// （例如转发循环被改成每条消息复制大对象、或引入串行化瓶颈），而不是断言绝对值。
        /// </summary>
        [TestMethod]
        public async Task TargetToClientPumpHoldsThroughput()
        {
            var payload = RandomBytes(64 * MiB, 7);

            using var server = new TestTargetServer();
            await using var tunnel = Tunnel.Start(server.Uri);
            using var target = await server.AcceptAsync(Timeout);
            await tunnel.ReadHandshakeAsync(Timeout);

            var writeTask = Task.Run(() => TestTargetServer.WriteExactlyAsync(target, payload, Timeout));

            var sw = Stopwatch.StartNew();
            var received = await tunnel.ReadPayloadAsync(payload.Length, Timeout);
            sw.Stop();

            await writeTask;
            AssertBytesEqual(payload, received);

            var mbps = payload.Length / (1024.0 * 1024.0) / sw.Elapsed.TotalSeconds;
            TestContext.WriteLine($"目标->客户端：{payload.Length / MiB} MiB / {sw.Elapsed.TotalSeconds:F2}s = {mbps:F1} MiB/s");
            Assert.IsTrue(mbps > 10, $"下行吞吐异常偏低：{mbps:F1} MiB/s");
        }

        /// <summary>客户端 -> 目标方向的吞吐回归，同样只看量级</summary>
        [TestMethod]
        public async Task ClientToTargetPumpHoldsThroughput()
        {
            var payload = RandomBytes(64 * MiB, 8);

            using var server = new TestTargetServer();
            await using var tunnel = Tunnel.Start(server.Uri);
            using var target = await server.AcceptAsync(Timeout);
            await tunnel.ReadHandshakeAsync(Timeout);

            var readTask = Task.Run(() => TestTargetServer.ReadExactlyAsync(target, payload.Length, Timeout));

            var sw = Stopwatch.StartNew();
            foreach (var chunk in Chunked(payload, 128 * 1024))
            {
                tunnel.Send(chunk);
            }
            tunnel.CompleteClientStream();
            sw.Stop();

            AssertBytesEqual(payload, await readTask);

            var mbps = payload.Length / (1024.0 * 1024.0) / sw.Elapsed.TotalSeconds;
            TestContext.WriteLine($"客户端->目标：{payload.Length / MiB} MiB / {sw.Elapsed.TotalSeconds:F2}s = {mbps:F1} MiB/s");
            Assert.IsTrue(mbps > 10, $"上行吞吐异常偏低：{mbps:F1} MiB/s");
        }

        // ---------------- 辅助 ----------------

        private static byte[] RandomBytes(int length, int seed)
        {
            var buffer = new byte[length];
            new Random(seed).NextBytes(buffer);
            return buffer;
        }

        private static IEnumerable<byte[]> Chunked(byte[] data, int size)
        {
            var offset = 0;
            while (offset < data.Length)
            {
                var len = Math.Min(size, data.Length - offset);
                var chunk = new byte[len];
                Array.Copy(data, offset, chunk, 0, len);
                offset += len;
                yield return chunk;
            }
        }

        private static void AssertBytesEqual(byte[] expected, byte[] actual)
        {
            Assert.AreEqual(expected.Length, actual.Length, "字节数不一致");
            for (var i = 0; i < expected.Length; i++)
            {
                Assert.AreEqual(expected[i], actual[i], $"第 {i} 字节不一致");
            }
        }
    }
}
