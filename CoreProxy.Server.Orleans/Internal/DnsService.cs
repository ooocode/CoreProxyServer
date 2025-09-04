using System.Net;

namespace CoreProxy.Server.Orleans.Internal
{
    public static class DnsService
    {
        public static async Task<IPAddress[]> GetIpAddressesAsync(string host, CancellationToken cancellationToken)
        {
            if (IPAddress.TryParse(host, out var addr))
            {
                return [addr];
            }

            return await Dns.GetHostAddressesAsync(host, cancellationToken);
        }
    }
}
