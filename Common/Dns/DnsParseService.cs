using System.Net;
using Microsoft.Extensions.Caching.Memory;

public class DnsParseService(IMemoryCache memoryCache)
{
    public async Task<IPAddress> GetIpAsync(string hostName, int port, CancellationToken cancellationToken = default)
    {
        if (memoryCache.TryGetValue<IPAddress>(hostName, out var ip) && ip != null)
        {
            //有效的缓存值
            return ip;
        }

        var ipAddresses = await Dns.GetHostAddressesAsync(hostName, cancellationToken);
        var iPAddress = ipAddresses.FirstOrDefault();
        ArgumentNullException.ThrowIfNull(iPAddress, nameof(iPAddress));

        if (ipAddresses.Length > 1)
        {
            await DnsBackgroundServiceService.DnsChannel.Writer.WriteAsync(new DnsItem
            {
                HostName = hostName,
                Port = port,
                IPAddresses = ipAddresses.ToList()
            }, cancellationToken);
        }

        return iPAddress;
    }
}