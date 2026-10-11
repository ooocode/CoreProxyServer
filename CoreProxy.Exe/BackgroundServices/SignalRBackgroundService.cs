using CoreProxy.ViewModels;
using Microsoft.AspNetCore.Http.Connections.Client;
using Microsoft.AspNetCore.SignalR.Client;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using System.Net;

namespace CoreProxy.BackgroundServices
{
    public class SignalRBackgroundService(ILogger<SignalRBackgroundService> logger) : BackgroundService

    {
        protected override async Task ExecuteAsync(CancellationToken stoppingToken)
        {
            const string serverHttpAddress = "https://localhost:6099";
            await using HubConnection hubConnection = new HubConnectionBuilder()
                     .WithAutomaticReconnect(new InfiniteReconnectPolicy())
                     .WithUrl(serverHttpAddress + "/chathub", (HttpConnectionOptions opt) =>
                     {
                         opt.HttpMessageHandlerFactory = (c) =>
                         {
                             HttpClientHandler httpClientHandler = (c as HttpClientHandler)!;
                             httpClientHandler.ServerCertificateCustomValidationCallback = (__, _, ___, errors) => true;
                             return c;
                         };

                         opt.WebSocketConfiguration = (c) =>
                         {
                             c.HttpVersion = HttpVersion.Version20;
                             c.HttpVersionPolicy = HttpVersionPolicy.RequestVersionExact;
                             /*if (serverItem.RequestHttpVersion == RequestHttpVersion.Auto)
                             {
                                 c.HttpVersion = HttpVersion.Version11;
                                 c.HttpVersionPolicy = HttpVersionPolicy.RequestVersionOrHigher;
                             }
                             else if (serverItem.RequestHttpVersion == RequestHttpVersion.Version11)
                             {
                                 c.HttpVersion = HttpVersion.Version11;
                                 c.HttpVersionPolicy = HttpVersionPolicy.RequestVersionExact;
                             }
                             else if (serverItem.RequestHttpVersion == RequestHttpVersion.Version20)
                             {
                                 c.HttpVersion = HttpVersion.Version20;
                                 c.HttpVersionPolicy = HttpVersionPolicy.RequestVersionExact;
                             }
                             else if (serverItem.RequestHttpVersion == RequestHttpVersion.Version30)
                             {
                                 c.HttpVersion = HttpVersion.Version30;
                                 c.HttpVersionPolicy = HttpVersionPolicy.RequestVersionExact;
                             }*/
                         };
                     })
                  .AddJsonProtocol(config => config.PayloadSerializerOptions = AppJsonSerializerContext.Default.Options).Build();

            while (!stoppingToken.IsCancellationRequested)
            {
                try
                {
                    await hubConnection.StartAsync(stoppingToken);
                    break;
                }
                catch (Exception ex)
                {
                    logger.LogError(ex, "SignalRBackgroundService");
                    await Task.Delay(5000, stoppingToken);
                }
            }

            await Task.Delay(Timeout.Infinite, stoppingToken);
        }
    }

    public class InfiniteReconnectPolicy : IRetryPolicy
    {
        public TimeSpan? NextRetryDelay(RetryContext retryContext)
        {
            return TimeSpan.FromSeconds(5);
        }
    }
}