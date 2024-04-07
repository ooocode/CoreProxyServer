using DnsClient;
using DnsClient.Protocol;
using System.Linq;
using System.Net;
using System.Threading;
using System.Threading.Tasks;

namespace ServerWebApplication.Common
{
    public class DnsParserService
    {
        private static readonly LookupClient lookupClient =
              new LookupClient(new LookupClientOptions() { UseCache = true });

        public async Task<IPAddress?> ParseIpAddressAsync(string domainOrIp,
            CancellationToken cancellationToken)
        {
            var ls = await lookupClient
                .QueryAsync(domainOrIp, QueryType.A, cancellationToken: cancellationToken);
            var record = ls
                .Answers.OfType<HInfoRecord>()
                .FirstOrDefault();

            if (record == null)
            {
                return null;
            }

            return null;
        }
    }
}
