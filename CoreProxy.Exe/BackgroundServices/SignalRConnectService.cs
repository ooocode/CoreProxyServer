using Microsoft.AspNetCore.SignalR.Client;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace CoreProxy.BackgroundServices
{
    internal class SignalRConnectBackgroundService(ILogger<SignalRConnectBackgroundService> logger) : BackgroundService
    {
        protected override async Task ExecuteAsync(CancellationToken stoppingToken)
        {
            return;
            while (!stoppingToken.IsCancellationRequested)
            {
                await Task.Delay(1000, stoppingToken);
                foreach (var hubConnection in GloHubConnections.Hubs)
                {
                    if (hubConnection.State == HubConnectionState.Disconnected)
                    {
                        try
                        {
                            await hubConnection.StartAsync(stoppingToken);
                        }
                        catch (Exception ex)
                        {
                            logger.LogError(ex, "连接signalr失败");
                            await Task.Delay(5000);
                        }
                    }
                }


                if (GloHubConnections.Hubs.All(x => x.State == HubConnectionState.Connected))
                {
                    break;
                }
            }
        }

        private Task HubConnection_Reconnecting(Exception? arg)
        {
            logger.LogError("signalR正在重连");
            return Task.CompletedTask;
        }

        public override async Task StopAsync(CancellationToken cancellationToken)
        {
            await base.StopAsync(cancellationToken);
        }
    }
}
