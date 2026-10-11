using CommunityToolkit.HighPerformance.Buffers;
using CoreProxy.BackgroundServices;
using CoreProxy.ViewModels;
using DotNext.IO.Pipelines;
using Grpc.Core;
using Microsoft.AspNetCore.Connections;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Http.Connections.Client;
using Microsoft.AspNetCore.SignalR.Client;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Net.Http.Headers;
using System;
using System.Buffers.Text;
using System.IO.Pipelines;
using System.Net;
using System.Net.Http;
using System.Net.ServerSentEvents;
using System.Runtime.CompilerServices;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Channels;

namespace CoreProxy.Internal
{
    public partial class SignalRTransportHandlerService(
        ILogger<SignalRTransportHandlerService> logger,
        IHttpClientFactory httpClientFactory)
    {
        private static readonly byte[] Socket5ConnectOkReturnData = [0x05, 0x00, 0x00, 0x01, 0x7f, 0x00, 0x00, 0x01, 0x1f, 0x40];
        private static readonly byte[] HttpConnectOkReturnData = Encoding.UTF8.GetBytes("HTTP/1.1 200 Connection Established\r\n\r\n");

        [LoggerMessage(Level = LogLevel.Information, Message = "开始连接 {connectionId} {targetAddress}:{targetPort}", EventId = StatusCodes.Status200OK)]
        private static partial void LogStartConnectTo(ILogger logger, string connectionId, string targetAddress, int targetPort);

        [LoggerMessage(Level = LogLevel.Information, Message = "成功连接到 {connectionId} {targetAddress}:{targetPort} 毫秒:{elapsedMilliseconds}", EventId = StatusCodes.Status201Created)]
        private static partial void LogSuccessConnectTo(ILogger logger, string connectionId, string targetAddress, int targetPort, long elapsedMilliseconds);

        [LoggerMessage(Level = LogLevel.Error, Message = "【WhenEach-Item】{targetAddress}:{targetPort} {errorMessage}\n{stackTrace}", EventId = StatusCodes.Status400BadRequest)]
        private static partial void LogException(ILogger logger, string targetAddress, int targetPort, string errorMessage, string? stackTrace);



        /// <summary>
        /// 浏览器tcp转发
        /// </summary>
        /// <param name="browser"></param>
        /// <param name="targetAddress"></param>
        /// <param name="targetPort"></param>
        /// <param name="proxyType"></param>
        /// <param name="cancellationToken"></param>
        public async Task DualStreamHandlerAsync(
                                                 ServerItem serverItem,
                                                 ConnectionContext browser,
                                                 string targetAddress,
                                                 int targetPort,
                                                 ProxyType proxyType,
                                                 CancellationToken cancellationToken)
        {
            logger.LogInformation($"开始连接：{targetAddress}:{targetPort}");

            const string serverHttpAddress = "http://oa.zwovo.xyz:19981";
            await using HubConnection hubConnection = new HubConnectionBuilder()
                     .WithAutomaticReconnect(new InfiniteReconnectPolicy())
                     .WithUrl(serverHttpAddress + "/StreamHub", (HttpConnectionOptions opt) =>
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


            await hubConnection.StartAsync(cancellationToken);

            var clientStream = ReadBrowserDataAsync(browser, cancellationToken);
            var s =  hubConnection.StreamAsync<string>("EchoStream",
                targetAddress, targetPort.ToString(),
                clientStream,
                cancellationToken);

            await foreach(var result in s)
            {
                if (result.StartsWith("id:"))
                {
                    var okData = proxyType == ProxyType.Socket5 ? Socket5ConnectOkReturnData : HttpConnectOkReturnData;
                    await browser.Transport.Output.WriteAsync(okData, cancellationToken);
                    continue;
                }
                var bytes = Convert.FromBase64String(result);
                await browser.Transport.Output.WriteAsync(bytes);
            }
        }

        /// <summary>
        /// 读浏览器数据
        /// </summary>
        /// <param name="browser"></param>
        /// <param name="cancellationToken"></param>
        /// <returns></returns>
        private static async IAsyncEnumerable<string> ReadBrowserDataAsync(
            ConnectionContext browser,
            [EnumeratorCancellation]CancellationToken cancellationToken)
        {
            Stat.ClientCount.Inc();

            try
            {
                await foreach (var item in browser.Transport.Input.ReadAllAsync(cancellationToken))
                {
                    yield return Convert.ToBase64String(item.ToArray());
                }
            }
            finally
            {
                Stat.ClientCount.Dec();
            }
        }
    }
}
