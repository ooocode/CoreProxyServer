using Grpc.Core;
using Microsoft.Extensions.Caching.Memory;
using System.Net;
using System.Net.Sockets;

namespace CoreProxy.Server.Orleans.Internal
{
    /// <summary>
    /// 目标主机名解析器，带 TTL 缓存（基于 net11 的 <see cref="DnsResolver"/>）。
    /// <para>
    /// 代理场景下大量隧道的目标主机是同一个后端：每建一条隧道都做一次 DNS 是纯粹的重复开销，
    /// 一次解析要付出 10~100ms+ 的网络往返，且完全挡在"发握手包"之前的建连关键路径上。
    /// 缓存后同一主机在 TTL 内只解析一次，隧道建立延迟只跟 TCP 握手有关。
    /// </para>
    /// <para>
    /// 默认走 net11 新增的 <see cref="DnsResolver"/>（而不是静态 <see cref="Dns.GetHostAddressesAsync(string, CancellationToken)"/>），
    /// 用 114.114.114.114 公共 DNS。地址选择固定取一条：优先 IPv4，缺则回退 IPv6
    /// （与旧行为一致，不做多 A 记录的轮询）。
    /// </para>
    /// </summary>
    /// <param name="memoryCache">DNS 结果缓存；同一主机在 TTL 内只解析一次</param>
    /// <param name="ttl">缓存有效期；<see cref="TimeSpan.Zero"/> 表示不缓存（每次解析）</param>
    public sealed class TargetResolver(
        IMemoryCache memoryCache,
        TimeSpan ttl)
    {
        private static readonly DnsResolver dnsResolver = new();

        /// <summary>
        /// 解析目标 IP：字面量直接使用，域名走缓存/DNS，优先 IPv4、缺则 IPv6。
        /// 解析不出可用地址（NXDOMAIN / 无 A、AAAA 记录）时与旧实现一致，返回 InvalidArgument。
        /// </summary>
        public async Task<IPAddress> ResolveAsync(string host, CancellationToken cancellationToken)
        {
            //客户端直接传 IP 时省去一次 DNS 解析（也无需进缓存）
            if (IPAddress.TryParse(host, out var literal))
            {
                return literal;
            }

            //TTL=0 表示不缓存：直接解析，绕过 MemoryCache
            if (ttl <= TimeSpan.Zero)
            {
                return await ResolveUncachedAsync(host, cancellationToken).ConfigureAwait(false);
            }

            return await memoryCache.GetOrCreateAsync(host,
                async _ => await ResolveUncachedAsync(host, cancellationToken).ConfigureAwait(false),
                new MemoryCacheEntryOptions { AbsoluteExpirationRelativeToNow = ttl }).ConfigureAwait(false)
                //解析工厂内已在无可用地址时抛 InvalidArgument，命中缓存必非空；这里是编译器可空兜底
                ?? throw new RpcException(new Status(StatusCode.InvalidArgument, host));
        }

        /// <summary>解析并挑选一个可用地址；无可用地址抛 InvalidArgument</summary>
        private static async Task<IPAddress> ResolveUncachedAsync(string host, CancellationToken cancellationToken)
        {
            var result = await dnsResolver.ResolveAddressesAsync(host, cancellationToken).ConfigureAwait(false);

            //非 NoError（如 NXDomain）或没有任何记录都算"解析不出可用地址"→ InvalidArgument
            if (result.ResponseCode != DnsResponseCode.NoError || result.Records.Count == 0)
            {
                throw new RpcException(new Status(StatusCode.InvalidArgument, host));
            }

            return PickPreferred(result.Records.Select(static r => r.Address))
                   ?? throw new RpcException(new Status(StatusCode.InvalidArgument, host));
        }

        /// <summary>优先 IPv4，其次 IPv6；两者都没有返回 null。</summary>
        internal static IPAddress? PickPreferred(IEnumerable<IPAddress> addresses)
            => addresses.FirstOrDefault(static x => x.AddressFamily == AddressFamily.InterNetwork)
               ?? addresses.FirstOrDefault(static x => x.AddressFamily == AddressFamily.InterNetworkV6);
    }
}
