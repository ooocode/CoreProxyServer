using DnsClient;
using System.Net;

namespace ServerWebApplication.Services
{
    public class DnsParseService
    {
        public static readonly LookupClient LookupClient = new(NameServer.GooglePublicDns,
            NameServer.GooglePublicDns2,
            NameServer.Cloudflare,
            NameServer.Cloudflare2);

        private static async Task<IPAddress?> ParseByDnsClientAsync(string hostName, CancellationToken cancellationToken)
        {
            var result = await LookupClient.QueryAsync(hostName,
               queryType: QueryType.ANY,
               cancellationToken: cancellationToken);
            var records = result.Answers.ARecords().ToList();

            var ipv4 = records.FirstOrDefault(x => x.RecordType == DnsClient.Protocol.ResourceRecordType.A);
            if (ipv4 != null)
            {
                return ipv4.Address;
            }

            var ipv6 = records.FirstOrDefault(x => x.RecordType == DnsClient.Protocol.ResourceRecordType.AAAA);
            if (ipv6 != null)
            {
                return ipv6.Address;
            }

            return null;
        }

        public async Task<IPAddress> GetIpAsync(string hostName, int _, CancellationToken cancellationToken = default)
        {
            var iPAddress = await ParseByDnsClientAsync(hostName, cancellationToken);
            if (iPAddress == null)
            {
                var ipAddresses = await Dns.GetHostAddressesAsync(hostName, cancellationToken);
                iPAddress = ipAddresses.FirstOrDefault(x => x.AddressFamily == System.Net.Sockets.AddressFamily.InterNetwork);
                iPAddress ??= ipAddresses.FirstOrDefault(x => x.AddressFamily == System.Net.Sockets.AddressFamily.InterNetworkV6);
            }

            ArgumentNullException.ThrowIfNull(iPAddress, nameof(iPAddress));
            return iPAddress;
        }
    }
}