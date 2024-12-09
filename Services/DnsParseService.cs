using System.Net;

namespace ServerWebApplication.Services
{
    public class DnsParseService
    {
        public async Task<IPAddress> GetIpAsync(string hostName, int _, CancellationToken cancellationToken = default)
        {
            var ipAddresses = await Dns.GetHostAddressesAsync(hostName, cancellationToken);
            var iPAddress = ipAddresses.FirstOrDefault(x => x.AddressFamily == System.Net.Sockets.AddressFamily.InterNetwork);
            iPAddress ??= ipAddresses.FirstOrDefault(x => x.AddressFamily == System.Net.Sockets.AddressFamily.InterNetworkV6);
            ArgumentNullException.ThrowIfNull(iPAddress, nameof(iPAddress));

            return iPAddress;
        }
    }
}