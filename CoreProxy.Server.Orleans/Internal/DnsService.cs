using System.Net;

namespace ServerWebApplication.Common
{
    public partial class DnsService
    {
        [LoggerMessage(Level = LogLevel.Information, Message = "成功解析IPV6：{hostName} -> {ipAddress}")]
        private static partial void LogDnsParseInfoV6(ILogger logger, string hostName, string ipAddress);

        [LoggerMessage(Level = LogLevel.Information, Message = "成功解析IPV4：{hostName} -> {ipAddress}")]
        private static partial void LogDnsParseInfoV4(ILogger logger, string hostName, string ipAddress);

        public static async Task<IPEndPoint> GetIpEndpointAsync(string host, int port, CancellationToken cancellationToken)
        {
            if (IPAddress.TryParse(host, out var addr))
            {
                return new IPEndPoint(addr, port);
            }

            var ipAddresses = await Dns.GetHostAddressesAsync(host, cancellationToken);
            var iPAddress = ipAddresses.FirstOrDefault(x => x.AddressFamily == System.Net.Sockets.AddressFamily.InterNetwork);
            iPAddress ??= ipAddresses.FirstOrDefault(x => x.AddressFamily == System.Net.Sockets.AddressFamily.InterNetworkV6);

            ArgumentNullException.ThrowIfNull(iPAddress, nameof(iPAddress));

            return new IPEndPoint(iPAddress, port);
        }

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
