
using System.Net;
using System.Threading.Channels;

public class DnsItem
{
    public required string HostName { get; set; }
    public required int Port { get; set; }

    public required List<IPAddress> IPAddresses { get; set; }
}

public class DnsBackgroundServiceService : BackgroundService
{
    public static Channel<DnsItem> DnsChannel = Channel.CreateBounded<DnsItem>(new BoundedChannelOptions(15)
    {
        FullMode = BoundedChannelFullMode.DropOldest
    });

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        await foreach (var item in DnsChannel.Reader.ReadAllAsync(stoppingToken))
        {

        }
    }
}