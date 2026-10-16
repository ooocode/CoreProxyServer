using CoreProxy.Server.Orleans.Internal;
using Grpc.Core;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.Internal;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using System.Net;

namespace CoreProxy.Server.Orleans.Tests
{
    /// <summary>
    /// TargetResolver 测试。
    /// <para>
    /// IP 选择的确定性逻辑（Pv4 优先 / IPv6 回退）与 IP 字面量直返不依赖网络，可复现；
    /// 涉及真实 DNS 的用例（NXDomain → InvalidArgument、TTL 过期重解析）结果由 DNS 语义保证
    /// （.invalid 固定 NXDomain、baidu.com 固定可解析），不关心具体 IP 值。
    /// </para>
    /// </summary>
    [TestClass]
    public sealed class TargetResolverTests
    {
        private static readonly IPAddress V4 = IPAddress.Parse("10.0.0.1");
        private static readonly IPAddress V6 = IPAddress.Parse("2001:db8::1");

        /// <summary>MemoryCache 的假时钟，用来确定性地推进绝对过期时间</summary>
        private sealed class ManualSystemClock : ISystemClock
        {
            public DateTimeOffset UtcNow { get; private set; } = DateTimeOffset.UnixEpoch;

            public void Advance(TimeSpan delta) => UtcNow += delta;
        }

        /// <summary>新造一个用假时钟 + 开统计的 MemoryCache 装配的 TargetResolver（TTL 60s）</summary>
        private static (TargetResolver Resolver, ManualSystemClock Clock, MemoryCache Cache) Create()
        {
            var clock = new ManualSystemClock();
            var cache = new MemoryCache(new MemoryCacheOptions { Clock = clock, TrackStatistics = true, ExpirationScanFrequency = TimeSpan.FromMilliseconds(1) });
            return (new TargetResolver(cache, TimeSpan.FromSeconds(60)), clock, cache);
        }

        // ---------------- IP 选择逻辑（纯函数，无网络） ----------------

        [TestMethod]
        public void Ipv4IsPreferredEvenWhenListedSecond()
            => Assert.AreEqual(V4, TargetResolver.PickPreferred([V6, V4]));

        [TestMethod]
        public void Ipv6IsPickedWhenNoIpv4Exists()
            => Assert.AreEqual(V6, TargetResolver.PickPreferred([V6]));

        [TestMethod]
        public void NoAddressesReturnsNull()
            => Assert.IsNull(TargetResolver.PickPreferred(Array.Empty<IPAddress>()));

        // ---------------- IP 字面量直返（无网络，不进缓存） ----------------

        [TestMethod]
        public async Task LiteralIpBypassesResolverAndCache()
        {
            var (resolver, _, cache) = Create();

            Assert.AreEqual(IPAddress.Loopback, await resolver.ResolveAsync("127.0.0.1", CancellationToken.None));
            Assert.AreEqual(IPAddress.IPv6Loopback, await resolver.ResolveAsync("::1", CancellationToken.None));

            Assert.AreEqual(0, cache.GetCurrentStatistics()!.CurrentEntryCount, "IP 字面量不应写入缓存");
        }

        // ---------------- 解析失败（走真实 DnsResolver，结果由 DNS 语义保证） ----------------

        [TestMethod]
        public async Task NxDomainHostThrowsInvalidArgument()
        {
            //.invalid 是 RFC 2606 保留后缀，任何解析器都固定返回 NXDOMAIN
            var (resolver, _, _) = Create();

            var exception = await Assert.ThrowsExceptionAsync<RpcException>(
                () => resolver.ResolveAsync("does-not-exist.invalid", CancellationToken.None));

            Assert.AreEqual(StatusCode.InvalidArgument, exception.Status.StatusCode);
        }

        // ---------------- TTL 缓存（走真实 DnsResolver 预热，用假时钟测过期） ----------------

        [TestMethod]
        public async Task TtlHitsCacheUntilExpiryThenReparses()
        {
            var (resolver, clock, cache) = Create();

            var ip1 = await resolver.ResolveAsync("baidu.com", CancellationToken.None);
            Assert.IsNotNull(ip1);
            Assert.AreEqual(1, cache.GetCurrentStatistics()!.CurrentEntryCount, "首次解析后应写入一条缓存");
            Assert.AreEqual(1, cache.GetCurrentStatistics()!.TotalMisses);

            //TTL 到期前：命中缓存，不重新解析
            var ip2 = await resolver.ResolveAsync("baidu.com", CancellationToken.None);
            Assert.AreEqual(ip1, ip2, "TTL 内应命中缓存返回同一地址");
            Assert.IsTrue(cache.GetCurrentStatistics()!.TotalHits >= 1, "TTL 内应命中缓存");

            //推进超过 60s：缓存过期，重新解析（miss 计数增加）
            clock.Advance(TimeSpan.FromSeconds(61));
            var ip3 = await resolver.ResolveAsync("baidu.com", CancellationToken.None);
            Assert.IsNotNull(ip3);
            Assert.IsTrue(cache.GetCurrentStatistics()!.TotalMisses >= 2, "过期后应重新解析");
        }
    }
}
