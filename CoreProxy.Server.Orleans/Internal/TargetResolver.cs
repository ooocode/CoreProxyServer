using Grpc.Core;
using System.Collections.Concurrent;
using System.Net;
using System.Net.Sockets;

namespace CoreProxy.Server.Orleans.Internal
{
    /// <summary>
    /// 目标主机名解析器，带 TTL 缓存。
    /// <para>
    /// 代理场景下大量隧道的目标主机是同一个后端：每建一条隧道都做一次 DNS 是纯粹的重复开销，
    /// 一次解析要付出 10~100ms+ 的网络往返，且完全挡在"发握手包"之前的建连关键路径上。
    /// 缓存后同一主机在 TTL 内只解析一次，隧道建立延迟只跟 TCP 握手有关。
    /// </para>
    /// <para>
    /// 与旧行为的一致性：旧的 <c>Array.Find(addresses, x =&gt; x.AddressFamily == InterNetwork)</c>
    /// 本来就固定取第一个 IPv4，从不做多个 A 记录的轮询，所以缓存不会损失负载分散能力，
    /// 唯一的代价是后端 IP 变更后最多滞后一个 TTL 才被感知。
    /// </para>
    /// <para>
    /// 并发冷启动（TTL 刚过、多条隧道同时打到同一主机）可能触发多于一次解析。
    /// 这里刻意不做单飞（single-flight）：重复解析只是多一次 DNS 查询，不影响正确性，
    /// 换来的是明显更简单的并发模型；同一主机的稳态请求全部命中缓存。
    /// </para>
    /// </summary>
    /// <param name="ttl">缓存有效期；<see cref="TimeSpan.Zero"/> 表示不缓存（每次解析）</param>
    /// <param name="resolveAsync">解析实现，默认走真实 DNS；测试可注入替身</param>
    /// <param name="timeProvider">时间源，默认系统时钟；TTL 相关测试可注入假时钟</param>
    public sealed class TargetResolver(
        TimeSpan ttl,
        Func<string, CancellationToken, Task<IPAddress[]>>? resolveAsync = null,
        TimeProvider? timeProvider = null)

    {
        private readonly ConcurrentDictionary<string, CacheEntry> cache = new(StringComparer.OrdinalIgnoreCase);
        private readonly Func<string, CancellationToken, Task<IPAddress[]>> resolveAsync = resolveAsync ?? ((host, cancellationToken) => Dns.GetHostAddressesAsync(host, cancellationToken));
        private readonly TimeProvider timeProvider = timeProvider ?? TimeProvider.System;


        /// <summary>当前缓存条目数（诊断与测试用）</summary>
        public int CacheCount => cache.Count;

        /// <summary>
        /// 解析目标 IP：字面量直接使用，域名走缓存/DNS，优先 IPv4。
        /// 解析不出可用地址时与旧实现一致，返回 InvalidArgument。
        /// </summary>
        public async Task<IPAddress> ResolveAsync(string host, CancellationToken cancellationToken)
        {
            //客户端直接传 IP 时省去一次 DNS 解析（也无需进缓存）
            if (IPAddress.TryParse(host, out var literal))
            {
                return literal;
            }

            if (ttl > TimeSpan.Zero)
            {
                var now = timeProvider.GetUtcNow();
                if (cache.TryGetValue(host, out var entry) && entry.ExpiresAt > now)
                {
                    return entry.Address;
                }
            }

            var addresses = await resolveAsync(host, cancellationToken).ConfigureAwait(false);
            var address = PickPreferred(addresses) ?? throw new RpcException(new Status(StatusCode.InvalidArgument, host));

            if (ttl > TimeSpan.Zero)
            {
                //解析本身耗时，过期时间从解析完成时刻起算，避免把解析耗时算进 TTL
                cache[host] = new CacheEntry(address, timeProvider.GetUtcNow() + ttl);
            }

            return address;
        }

        /// <summary>优先 IPv4，其次 IPv6；两者都没有返回 null</summary>
        private static IPAddress? PickPreferred(IPAddress[] addresses)
        {
            var ipv4 = Array.Find(addresses, static x => x.AddressFamily == AddressFamily.InterNetwork);
            if (ipv4 is not null)
            {
                return ipv4;
            }

            return Array.Find(addresses, static x => x.AddressFamily == AddressFamily.InterNetworkV6);
        }

        private readonly record struct CacheEntry(IPAddress Address, DateTimeOffset ExpiresAt);
    }
}
