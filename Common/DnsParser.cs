using DnsClient;
using System.Net;
using System.Threading;
using System;
using System.Threading.Tasks;
using System.Linq;

namespace ServerWebApplication.Common
{
    public class DnsParserService
    {
        private static LookupClient lookupClient =
              new LookupClient(new LookupClientOptions() { UseCache = true });

        public async Task<IPAddress> ParseIpAddressAsync(string domainOrIp, CancellationToken cancellationToken)
        {
            if (!IPAddress.TryParse(domainOrIp, out var iPAddress))
            {
                var record = (await lookupClient.QueryAsync(domainOrIp, QueryType.A,
                    cancellationToken: cancellationToken)).Answers.ARecords().FirstOrDefault();
                if (record == null)
                {
                    throw new Exception($"不能连接到WebServer " +
                        $"{domainOrIp},因为无法解析DNS");
                }

                iPAddress = record.Address;
            }

            return iPAddress;
        }
    }
}
