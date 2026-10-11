using CoreProxy.ViewModels;
using Grpc.Net.ClientFactory;
using Hello;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace CoreProxy.BackgroundServices
{
    public partial class StatBackgroundService(GrpcClientFactory grpcClientFactory,
        ILogger<StatBackgroundService> logger,
        List<ServerItem> serverItems,
        IHostEnvironment hostEnvironment) : BackgroundService
    {
        protected override async Task ExecuteAsync(CancellationToken stoppingToken)
        {
            var serverItem = serverItems.FirstOrDefault(x => x.LocalListenerPort == 1079);
            if (serverItem == null)
            {
                return;
            }

            var httpClient = grpcClientFactory.CreateClient<Greeter.GreeterClient>(serverItem.LocalListenerPort.ToString());
            var showServerDetail = hostEnvironment.IsDevelopment();

            while (!stoppingToken.IsCancellationRequested)
            {
                try
                {
                    var res = await httpClient.GetStatusAsync(new GetStatusRequest
                    {
                        IncludeDetail = showServerDetail
                    }, cancellationToken: stoppingToken);

                    ShowCounts(res.SocketCount.ToString(),
                        Stat.ClientCount.Value, Stat.ServerCount.Value);
                    if (res.Connections.Count > 0)
                    {
                        logger.LogInformation(string.Join("\r\n", res.Connections.Select(x => $"{x.DateTime.ToDateTimeOffset().ToLocalTime():HH:mm:ss} {x.IpAddress}")));
                    }
                }
                catch (Exception ex)
                {
                    logger.LogError(ex, "StatBackgroundService");
                }

                if (Environment.UserInteractive)
                {
                    await Task.Delay(1000 * 4, stoppingToken);
                }
                else
                {
                    await Task.Delay(1000 * 60, stoppingToken);
                }
            }
        }

        [LoggerMessage(
             EventId = 0,
             Level = LogLevel.Information,
             Message = "服务器：{connectingCount}\r\n客户端：{clientCount}-{serverCount}")]
        public partial void ShowCounts(string connectingCount, double clientCount, double serverCount);
    }
}
