using System.Net;
using DnsClient;
using Microsoft.Extensions.Caching.Memory;

public class DnsParseService(IMemoryCache memoryCache, ILookupClient lookupClient)
{
    public async Task<IPAddress> GetIpAsync(string hostName, int port, CancellationToken cancellationToken = default)
    {
        if (memoryCache.TryGetValue<IPAddress>(hostName, out var ip) && ip != null)
        {
            //有效的缓存值
            return ip;
        }

        var result = await lookupClient.QueryAsync(hostName, QueryType.A, cancellationToken: cancellationToken);
        var ipAddresses = result.Answers.ARecords().Select(x=>x.TimeToLive);
        var iPAddress = ipAddresses.FirstOrDefault()?.Address;
        ArgumentNullException.ThrowIfNull(iPAddress, nameof(iPAddress));
        await DnsBackgroundServiceService.DnsChannel.Writer.WriteAsync(new DnsItem
        {
            HostName = hostName,
            Port = port,
            IPAddresses = iPAddress
        })
        return iPAddress;
    }
}