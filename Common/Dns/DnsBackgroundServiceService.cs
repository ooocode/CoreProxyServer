
using System.Net;
using System.Net.Sockets;
using System.Threading.Channels;
using Microsoft.Extensions.Caching.Memory;

public class DnsItem
{
    public required string HostName { get; set; }
    public required int Port { get; set; }

    public required List<IPAddress> IPAddresses { get; set; }
}

public class DnsBackgroundServiceService(IMemoryCache memoryCache, ILogger<DnsBackgroundServiceService> logger) : BackgroundService
{
    public static Channel<DnsItem> DnsChannel = Channel.CreateBounded<DnsItem>(new BoundedChannelOptions(10)
    {
        FullMode = BoundedChannelFullMode.DropOldest
    });

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        await foreach (var item in DnsChannel.Reader.ReadAllAsync(stoppingToken))
        {
            using var cancellationTokenSource = new CancellationTokenSource(TimeSpan.FromSeconds(10));

            List<Task<IPAddress>> tasks = [];
            foreach (var ip in item.IPAddresses)
            {
                tasks.Add(ConnectToServer(ip, item.Port, cancellationTokenSource.Token));
            }

            try
            {
                await foreach (var task in Task.WhenEach(tasks).WithCancellation(cancellationTokenSource.Token).WithCancellation(stoppingToken))
                {
                    var ipAddress = await task;
                    memoryCache.Set(item.HostName, ipAddress, TimeSpan.FromMinutes(5));
                    cancellationTokenSource.Cancel();
                    break;
                }
            }
            catch (Exception ex)
            {
                logger.LogError(ex, "DnsBackgroundServiceService-Task.WhenEach");
            }
        }
    }

    private static async Task<IPAddress> ConnectToServer(IPAddress iPAddress, int port, CancellationToken cancellationToken)
    {
        using var tcpClient = new TcpClient();
        await tcpClient.ConnectAsync(iPAddress, port, cancellationToken);
        return iPAddress;
    }
}