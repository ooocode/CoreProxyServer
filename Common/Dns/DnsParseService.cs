using System.Net;

public class DnsParseService
{
    public async Task<IPAddress> GetIpAsync(string hostName, int port, CancellationToken cancellationToken = default)
    {
        var ipAddresses = await Dns.GetHostAddressesAsync(hostName, cancellationToken);
        var iPAddress = ipAddresses.FirstOrDefault(x => x.AddressFamily == System.Net.Sockets.AddressFamily.InterNetwork);
        ArgumentNullException.ThrowIfNull(iPAddress, nameof(iPAddress));

        return iPAddress;
    }
}