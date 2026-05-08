using CoreProxy.Server.Orleans.Internal;
using DotNext.IO.Pipelines;
using Microsoft.AspNetCore.Server.Kestrel.Transport.Sockets;
using Microsoft.AspNetCore.SignalR;
using Microsoft.Extensions.Hosting;
using System.Runtime.CompilerServices;
using System.Threading;
using System.Threading.Channels;
using System.Threading.Tasks;

namespace CoreProxy.Server.Orleans.Services
{
    public class StreamHub(IHostApplicationLifetime hostApplicationLifetime,
                     SocketConnectionContextFactory connectionFactory,
                    ILogger<StreamHub> logger) : Hub
    {
        // 双向流方法：接收 clientStream，返回 IAsyncEnumerable
        public ChannelReader<string> EchoStream(
            string host, string port,
            ChannelReader<string> clientStream)
        {
            if (logger.IsEnabled(LogLevel.Information))
            {
                logger.LogInformation("开始连接目标服务器 {host}:{port}", host, port);
            }

            using var cancellationSource = CancellationTokenSource.CreateLinkedTokenSource(
                      Context.ConnectionAborted, hostApplicationLifetime.ApplicationStopping);
            cancellationSource.CancelAfter(TimeSpan.FromHours(1));

            var serverChannel = Channel.CreateUnbounded<string>();
            _ = CoreHandler(host, int.Parse(port), clientStream, serverChannel, cancellationSource);
            return serverChannel.Reader;
        }

        private async Task CoreHandler(string host, int port, ChannelReader<string> clientStream, Channel<string> serverChannel,
            CancellationTokenSource cancellationTokenSource)
        {
            var cancellationTokenEx = cancellationTokenSource.Token;

            string connectionId = Guid.CreateVersion7().ToString("N");

            try
            {
                //添加连接信息
                GlobalState.Connections.TryAdd(connectionId, new ConnectItem
                {
                    ClientIpAddress = Context.GetHttpContext()?.Connection.RemoteIpAddress?.ToString() ?? string.Empty,
                    DateTime = DateTimeOffset.UtcNow
                });

                await using TcpConnectTargetServerService tcpConnectTargetServerService = new(connectionFactory, host, port);
                await tcpConnectTargetServerService.ConnectAsync(cancellationTokenEx);

                //发送id，表示连接成功
                await serverChannel.Writer.WriteAsync($"id:{connectionId}", cancellationTokenEx);

                //读取客户端数据
                var taskClient = HandlerClientAsync(tcpConnectTargetServerService, clientStream, serverChannel, cancellationTokenEx);
                var serverClient = HandlerServer(tcpConnectTargetServerService, serverChannel, cancellationTokenEx);

                var task = await Task.WhenAny(taskClient, serverClient);
                if (task.Id == serverClient.Id)
                {
                    // 情况 A：目标服务器主动断开了连接
                    logger.LogInformation("目标服务器已断开，等待客户端发送剩余数据...");

                    // 给客户端 3-5 秒的时间处理完它 Channel 里的剩余数据
                    // 注意：这里不再是盲等，而是配合 Task.WhenAny 或逻辑判断
                    using var timeoutCts = new CancellationTokenSource(TimeSpan.FromSeconds(5));
                    using var linkedCts = CancellationTokenSource.CreateLinkedTokenSource(timeoutCts.Token, cancellationTokenEx);

                    try
                    {
                        // 继续等待客户端任务完成，但受限于超时
                        await taskClient.WaitAsync(linkedCts.Token);
                    }
                    catch (OperationCanceledException)
                    {
                        logger.LogWarning("客户端发送超时或已断开");
                    }
                }

                await cancellationTokenSource.CancelAsync();
                await foreach (var item in Task.WhenEach(taskClient, serverClient))
                {
                    if (item.Exception is not null)
                    {
                        logger.LogError(item.Exception, "CoreHandler");
                    }
                }
            }
            finally
            {
                if (!cancellationTokenSource.IsCancellationRequested)
                {
                    await cancellationTokenSource.CancelAsync();
                }
                serverChannel.Writer.TryComplete();
                GlobalState.Connections.TryRemove(connectionId, out var _);
            }
        }


        private static async Task HandlerServer(TcpConnectTargetServerService tcpConnectTargetServerService,
            Channel<string> serverChannel,
            CancellationToken cancellationToken)
        {
            //读取目标服务器数据
            await foreach (var item in tcpConnectTargetServerService.connectionContext!.Transport.Input.ReadAllAsync(cancellationToken))
            {
                var str = Convert.ToBase64String(item.Span);
                await serverChannel.Writer.WriteAsync(str, cancellationToken);
            }
        }

        /// <summary>
        /// 处理客户端
        /// </summary>
        /// <param name="tcpConnectTargetServerService"></param>
        /// <param name="clientStream"></param>
        /// <param name="cancellationToken"></param>
        /// <returns></returns>
        private static async Task HandlerClientAsync(TcpConnectTargetServerService tcpConnectTargetServerService,
         ChannelReader<string> clientStream,
         Channel<string> serverChannel,
         CancellationToken cancellationToken)
        {
            //读取客户端数据
            await foreach (var item in clientStream.ReadAllAsync(cancellationToken))
            {
                await tcpConnectTargetServerService.SendAsync(Convert.FromBase64String(item), cancellationToken);
            }
        }

        public override Task OnConnectedAsync()
        {
            logger.LogInformation("客户端上线");
            return base.OnConnectedAsync();
        }

        public override Task OnDisconnectedAsync(Exception? exception)
        {
            logger.LogError("客户端下线");
            return base.OnDisconnectedAsync(exception);
        }
    }
}