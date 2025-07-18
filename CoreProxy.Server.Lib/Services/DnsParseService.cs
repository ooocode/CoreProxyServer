using System.Net;

namespace CoreProxy.Server.Lib.Services
{
    public class DnsParseService
    {
        //public static readonly LookupClient LookupClient = new(NameServer.GooglePublicDns,
        //    NameServer.GooglePublicDns2,
        //    NameServer.Cloudflare,
        //    NameServer.Cloudflare2);

        //private static async Task<IPAddress?> ParseByDnsClientAsync(string hostName, QueryType queryType, CancellationToken cancellationToken)
        //{
        //    var result = await LookupClient.QueryAsync(hostName,
        //       queryType: queryType,
        //       cancellationToken: cancellationToken);
        //    var records = result.Answers.ARecords().ToList();

        //    return records.FirstOrDefault()?.Address;
        //}

        public async Task<IPAddress> GetIpAsync(string hostName, int _, CancellationToken cancellationToken = default)
        {
            //var iPAddress = await ParseByDnsClientAsync(hostName, QueryType.A, cancellationToken);
            //if (iPAddress != null)
            //{
            //    return iPAddress;
            //}

            //iPAddress = await ParseByDnsClientAsync(hostName, QueryType.AAAA, cancellationToken);
            //if (iPAddress != null)
            //{
            //    return iPAddress;
            //}

            var ipAddresses = await Dns.GetHostAddressesAsync(hostName, cancellationToken);
            var iPAddress = ipAddresses.FirstOrDefault(x => x.AddressFamily == System.Net.Sockets.AddressFamily.InterNetwork);
            iPAddress ??= ipAddresses.FirstOrDefault(x => x.AddressFamily == System.Net.Sockets.AddressFamily.InterNetworkV6);

            ArgumentNullException.ThrowIfNull(iPAddress, nameof(iPAddress));
            return iPAddress;
        }
    }
}