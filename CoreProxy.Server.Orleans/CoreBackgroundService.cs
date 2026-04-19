
using CoreProxy.Server.Orleans.Internal;
using CoreProxy.Server.Orleans.Services;
using DotNext.Collections.Generic;
using Google.Protobuf;
using Grpc.Core;
using Hello;
using Microsoft.AspNetCore.Server.Kestrel.Transport.Sockets;
using Microsoft.Extensions.Hosting;
using System;
using System.Threading.Channels;

namespace CoreProxy.Server.Orleans
{
    public class CoreBackgroundService(SocketConnectionContextFactory connectionFactory, IConfiguration configuration) : BackgroundService
    {
        public static readonly Channel<CoreItem> channel = Channel.CreateUnbounded<CoreItem>();

        protected override async Task ExecuteAsync(CancellationToken stoppingToken)
        {
            if (!int.TryParse(configuration["MaxDegreeOfParallelism"], out int maxDegreeOfParallelism))
            {
                maxDegreeOfParallelism = int.MaxValue;
            }
            if (maxDegreeOfParallelism <= 0)
            {
                maxDegreeOfParallelism = int.MaxValue;
            }

            var source = channel.Reader.ReadAllAsync(stoppingToken);
            await Parallel.ForEachAsync(source, new ParallelOptions
            {
                CancellationToken = stoppingToken,
                MaxDegreeOfParallelism = maxDegreeOfParallelism,
            }, HandlerAsync);
        }

        private async ValueTask HandlerAsync(CoreItem coreItem, CancellationToken cancellationTokenApplicationStopping)
        {
            using var cancellationSource = CancellationTokenSource.CreateLinkedTokenSource(
                coreItem.CancellationToken, cancellationTokenApplicationStopping);
            var cancellationToken = cancellationSource.Token;

            try
            {
                if (coreItem.Logger.IsEnabled(LogLevel.Information))
                {
                    coreItem.Logger.LogInformation("开始连接目标服务器 {host}:{port}",
                        coreItem.Host, coreItem.Port);
                }

                await using TcpConnectTargetServerService tcpConnectTargetServerService = new(connectionFactory, coreItem.Host, coreItem.Port);
                await tcpConnectTargetServerService.ConnectAsync(cancellationToken);

                //发送空包，表示连接成功
                await coreItem.ResponseStream.WriteAsync(new()
                {
                    Payload = ByteString.Empty,
                    UnixTimeMilliseconds = MyGrpcService.GetUnixTimeMilliseconds()
                }, cancellationToken);

                LastActivityTime lastActivityTime = new()
                {
                    UnixTimeMilliseconds = MyGrpcService.GetUnixTimeMilliseconds()
                };
                var taskClient = MyGrpcService.HandlerClientAsync(lastActivityTime, tcpConnectTargetServerService, coreItem.RequestStream, cancellationToken);
                var taskServer = MyGrpcService.HandlerServerAsync(lastActivityTime, tcpConnectTargetServerService, coreItem.ResponseStream, cancellationToken);
                var taskCheck = MyGrpcService.CheckKeepAliveAsync(lastActivityTime, cancellationToken);

                var completedTask = await Task.WhenAny(taskClient, taskServer, taskCheck);
                if (completedTask.Id != taskCheck.Id && !cancellationToken.IsCancellationRequested)
                {
                    // 等待一小段时间，等待剩余数据处理
                    await Task.Delay(2000, CancellationToken.None);
                }
                cancellationSource.Cancel();

                await foreach (var item in Task.WhenEach(taskClient, taskServer, taskCheck))
                {
                    if (item.Exception is not null)
                    {
                        coreItem.Logger.LogError(item.Exception, "Task.WhenEach异常");
                    }
                }
            }
            catch (Exception ex)
            {
                coreItem.Logger.LogError(ex, "连接目标服务器 {host}:{port} 发生异常",
                    coreItem.Host, coreItem.Port);
            }
            finally
            {
                if (coreItem.Logger.IsEnabled(LogLevel.Information))
                {
                    coreItem.Logger.LogInformation("结束连接目标服务器 {host}:{port}",
                       coreItem.Host, coreItem.Port);
                }

                cancellationSource.Cancel();

                coreItem.TaskCompletionSource.SetResult();
            }
        }
    }

    public class CoreItem
    {
        public required ILogger Logger { get; set; }
        public required string ConnectionId { get; set; }
        public required string ClientIpAddress { get; set; }
        public required string Host { get; set; }
        public required int Port { get; set; }

        public required IAsyncStreamReader<HttpData> RequestStream { get; set; }
        public required IServerStreamWriter<HttpData> ResponseStream { get; set; }

        public required CancellationToken CancellationToken { get; set; }

        public required TaskCompletionSource TaskCompletionSource { get; set; }
    }
}