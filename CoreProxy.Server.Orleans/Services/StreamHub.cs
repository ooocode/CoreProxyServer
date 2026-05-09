using CoreProxy.Server.Orleans.Internal;
using DotNext.IO.Pipelines;
using Microsoft.AspNetCore.Connections;
using Microsoft.AspNetCore.SignalR;
using System.Net;
using System.Threading.Channels;

namespace CoreProxy.Server.Orleans.Services
{
    public class StreamHub(IHostApplicationLifetime hostApplicationLifetime,
                     IConnectionFactory connectionFactory,
                    ILogger<StreamHub> logger) : Hub
    {
        // 双向流方法：接收 clientStream，返回 IAsyncEnumerable
        public async IAsyncEnumerable<string> EchoStream(
            string host, string port,
            ChannelReader<string> clientStream)
        {
            string connectionId = Guid.CreateVersion7().ToString("N");

            using var cancellationSource = CancellationTokenSource.CreateLinkedTokenSource(
                        Context.ConnectionAborted, hostApplicationLifetime.ApplicationStopping);
            var cancellationToken = cancellationSource.Token;

            if (logger.IsEnabled(LogLevel.Information))
            {
                logger.LogInformation("开始连接目标服务器 {host}:{port}，ConnectionId:{connectionId}", host, port, connectionId);
            }

            var ips = await Dns.GetHostAddressesAsync(host, cancellationToken);
            var ip = ips.OrderBy(x => x.AddressFamily).First();

            await using var serverConnectionContext = await connectionFactory.ConnectAsync(new IPEndPoint(ip, int.Parse(port)), cancellationToken);

            //发送空包，表示连接成功
            yield return $"id:{connectionId}";

            //添加连接信息
            GlobalState.Connections.TryAdd(connectionId, new ConnectItem
            {
                ClientIpAddress = Context.GetHttpContext()?.Connection?.RemoteIpAddress?.ToString() ?? string.Empty,
                DateTime = DateTimeOffset.UtcNow
            });

            try
            {
                var taskClient = DotNext.Collections.Generic.AsyncEnumerable.ForEachAsync(
                    clientStream.ReadAllAsync(cancellationToken),
                    async (item, ct) => await serverConnectionContext.Transport.Output.WriteAsync(Convert.FromBase64String(item), ct),
                    cancellationToken).AsTask();

                await foreach (var item in serverConnectionContext.Transport.Input.ReadAllAsync(cancellationToken))
                {
                    yield return Convert.ToBase64String(item.Span);
                }

                await taskClient.WaitAsync(TimeSpan.FromSeconds(3), cancellationToken);
            }
            finally
            {
                GlobalState.Connections.TryRemove(connectionId, out var _);

                if (logger.IsEnabled(LogLevel.Information))
                {
                    logger.LogInformation("结束连接目标服务器 ConnectionId:{connectionId}", connectionId);
                }

                cancellationSource.Cancel();
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