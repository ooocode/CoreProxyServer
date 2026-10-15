using CoreProxy.Server.Orleans.Internal;
using Grpc.Core;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using System.Net;
using System.Net.Sockets;

namespace CoreProxy.Server.Orleans.Tests
{
    /// <summary>
    /// TargetResolver 的单元测试。全部使用注入的替身解析器与假时钟，不依赖真实 DNS，结果可复现。
    /// </summary>
    [TestClass]
    public sealed class TargetResolverTests
    {
        private static readonly IPAddress V4 = IPAddress.Parse("10.0.0.1");
        private static readonly IPAddress V6 = IPAddress.Parse("2001:db8::1");

        /// <summary>手动推进的时钟，用来确定性地测 TTL 过期</summary>
        private sealed class ManualTimeProvider : TimeProvider
        {
            private DateTimeOffset now = DateTimeOffset.UnixEpoch;

            public void Advance(TimeSpan delta) => now += delta;

            public override DateTimeOffset GetUtcNow() => now;

            public override long GetTimestamp() => now.UtcTicks;
        }

        /// <summary>记录调用次数的替身解析器</summary>
        private sealed class CountingResolver(params IPAddress[] result)
        {
            public int Calls;

            public Task<IPAddress[]> ResolveAsync(string host, CancellationToken cancellationToken)
            {
                Interlocked.Increment(ref Calls);
                return Task.FromResult(result);
            }
        }

        // ---------------- 地址选择 ----------------

        [TestMethod]
        public async Task Ipv4IsPreferredOverIpv6()
        {
            //故意把 IPv6 放在前面，验证选择逻辑不是"取数组第一个"
            var resolver = new TargetResolver(TimeSpan.FromSeconds(60), new CountingResolver(V6, V4).ResolveAsync);

            Assert.AreEqual(V4, await resolver.ResolveAsync("example.com", CancellationToken.None));
        }

        [TestMethod]
        public async Task Ipv6IsUsedWhenNoIpv4Exists()
        {
            var resolver = new TargetResolver(TimeSpan.FromSeconds(60), new CountingResolver(V6).ResolveAsync);

            Assert.AreEqual(V6, await resolver.ResolveAsync("example.com", CancellationToken.None));
        }

        [TestMethod]
        public async Task NoUsableAddressThrowsInvalidArgument()
        {
            var resolver = new TargetResolver(TimeSpan.FromSeconds(60), new CountingResolver().ResolveAsync);

            var exception = await Assert.ThrowsExceptionAsync<RpcException>(
                () => resolver.ResolveAsync("example.com", CancellationToken.None));
            Assert.AreEqual(StatusCode.InvalidArgument, exception.Status.StatusCode);
        }

        [TestMethod]
        public async Task LiteralIpBypassesResolutionAndCache()
        {
            var counting = new CountingResolver(V4);
            var resolver = new TargetResolver(TimeSpan.FromSeconds(60), counting.ResolveAsync);

            Assert.AreEqual(IPAddress.Loopback, await resolver.ResolveAsync("127.0.0.1", CancellationToken.None));
            Assert.AreEqual(IPAddress.IPv6Loopback, await resolver.ResolveAsync("::1", CancellationToken.None));

            Assert.AreEqual(0, counting.Calls, "IP 字面量不应触发 DNS");
            Assert.AreEqual(0, resolver.CacheCount, "IP 字面量不应进入缓存");
        }

        // ---------------- 缓存命中与过期 ----------------

        [TestMethod]
        public async Task RepeatedResolutionWithinTtlHitsCache()
        {
            var counting = new CountingResolver(V4);
            var resolver = new TargetResolver(TimeSpan.FromSeconds(60), counting.ResolveAsync);

            for (var i = 0; i < 10; i++)
            {
                Assert.AreEqual(V4, await resolver.ResolveAsync("example.com", CancellationToken.None));
            }

            Assert.AreEqual(1, counting.Calls, "TTL 内同一主机只应解析一次");
        }

        [TestMethod]
        public async Task ExpiredEntryIsResolvedAgain()
        {
            var counting = new CountingResolver(V4);
            var clock = new ManualTimeProvider();
            var resolver = new TargetResolver(TimeSpan.FromSeconds(60), counting.ResolveAsync, clock);

            await resolver.ResolveAsync("example.com", CancellationToken.None);
            Assert.AreEqual(1, counting.Calls);

            //TTL 边界内：仍命中缓存
            clock.Advance(TimeSpan.FromSeconds(59));
            await resolver.ResolveAsync("example.com", CancellationToken.None);
            Assert.AreEqual(1, counting.Calls, "TTL 未到不应重新解析");

            //过 TTL：重新解析
            clock.Advance(TimeSpan.FromSeconds(2));
            await resolver.ResolveAsync("example.com", CancellationToken.None);
            Assert.AreEqual(2, counting.Calls, "TTL 过后应重新解析");
        }

        [TestMethod]
        public async Task TtlCountsFromResolutionCompletion()
        {
            //解析本身耗时不能算进 TTL，否则慢解析会把缓存有效期吃掉
            var clock = new ManualTimeProvider();
            var calls = 0;
            var resolver = new TargetResolver(TimeSpan.FromSeconds(60), async (host, cancellationToken) =>
            {
                calls++;
                clock.Advance(TimeSpan.FromSeconds(30)); //模拟一次很慢的解析
                await Task.Yield();
                return [V4];
            }, clock);

            await resolver.ResolveAsync("example.com", CancellationToken.None);

            //从解析完成时刻起算，再走 30s 仍应在有效期内
            clock.Advance(TimeSpan.FromSeconds(30));
            await resolver.ResolveAsync("example.com", CancellationToken.None);
            Assert.AreEqual(1, calls, "解析耗时应被排除在 TTL 之外");
        }

        [TestMethod]
        public async Task ZeroTtlDisablesCaching()
        {
            var counting = new CountingResolver(V4);
            var resolver = new TargetResolver(TimeSpan.Zero, counting.ResolveAsync);

            await resolver.ResolveAsync("example.com", CancellationToken.None);
            await resolver.ResolveAsync("example.com", CancellationToken.None);

            Assert.AreEqual(2, counting.Calls, "TTL=0 应每次重新解析");
            Assert.AreEqual(0, resolver.CacheCount);
        }

        [TestMethod]
        public async Task DifferentHostsAreCachedIndependently()
        {
            var counting = new CountingResolver(V4);
            var resolver = new TargetResolver(TimeSpan.FromSeconds(60), counting.ResolveAsync);

            await resolver.ResolveAsync("a.example.com", CancellationToken.None);
            await resolver.ResolveAsync("b.example.com", CancellationToken.None);
            await resolver.ResolveAsync("a.example.com", CancellationToken.None);

            Assert.AreEqual(2, counting.Calls);
            Assert.AreEqual(2, resolver.CacheCount);
        }

        [TestMethod]
        public async Task HostCasingIsIgnoredForCacheKey()
        {
            //DNS 主机名大小写不敏感，大小写不同不应造成重复解析
            var counting = new CountingResolver(V4);
            var resolver = new TargetResolver(TimeSpan.FromSeconds(60), counting.ResolveAsync);

            await resolver.ResolveAsync("Example.COM", CancellationToken.None);
            await resolver.ResolveAsync("example.com", CancellationToken.None);

            Assert.AreEqual(1, counting.Calls);
        }

        // ---------------- 并发 ----------------

        [TestMethod]
        public async Task WarmCacheServesConcurrentCallersWithoutExtraResolution()
        {
            var counting = new CountingResolver(V4);
            var resolver = new TargetResolver(TimeSpan.FromSeconds(60), counting.ResolveAsync);

            await resolver.ResolveAsync("example.com", CancellationToken.None); //预热

            var results = await Task.WhenAll(Enumerable.Range(0, 64)
                .Select(_ => resolver.ResolveAsync("example.com", CancellationToken.None)));

            Assert.IsTrue(results.All(x => x.Equals(V4)));
            Assert.AreEqual(1, counting.Calls, "缓存命中路径不应再触发解析");
        }

        [TestMethod]
        public async Task ConcurrentColdStartIsSafeAndConsistent()
        {
            //冷启动并发可能解析多于一次（刻意不做单飞），但结果必须一致且不能崩
            var counting = new CountingResolver(V4);
            var resolver = new TargetResolver(TimeSpan.FromSeconds(60), counting.ResolveAsync);

            var results = await Task.WhenAll(Enumerable.Range(0, 64)
                .Select(_ => resolver.ResolveAsync("example.com", CancellationToken.None)));

            Assert.IsTrue(results.All(x => x.Equals(V4)), "并发冷启动的结果必须一致");
            Assert.IsTrue(counting.Calls >= 1, "至少解析一次");
            Assert.AreEqual(1, resolver.CacheCount, "缓存里只应有一条记录");
        }

        [TestMethod]
        public async Task CancellationPropagates()
        {
            using var cts = new CancellationTokenSource();
            await cts.CancelAsync();

            var resolver = new TargetResolver(TimeSpan.FromSeconds(60), (host, cancellationToken) =>
                Task.FromCanceled<IPAddress[]>(cancellationToken));

            await Assert.ThrowsExceptionAsync<TaskCanceledException>(
                () => resolver.ResolveAsync("example.com", cts.Token));
        }

        [TestMethod]
        public async Task AddressFamilyPreferenceMatchesLegacyBehaviour()
        {
            //旧实现固定取第一个 IPv4；这里确认行为一致（不做多 A 记录轮询）
            var resolver = new TargetResolver(TimeSpan.FromSeconds(60), new CountingResolver(V4, IPAddress.Parse("10.0.0.2")).ResolveAsync);

            Assert.AreEqual(V4, await resolver.ResolveAsync("example.com", CancellationToken.None));
        }
    }
}
