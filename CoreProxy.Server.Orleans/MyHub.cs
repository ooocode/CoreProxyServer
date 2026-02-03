using System.Buffers.Text;
using System.Runtime.CompilerServices;
using CoreProxy.Server.Orleans.Internal;
using Google.Protobuf;
using Hello;
using Microsoft.AspNetCore.Server.Kestrel.Transport.Sockets;
using Microsoft.AspNetCore.SignalR;

namespace Namespace2
{
    public class StreamHub(IHostApplicationLifetime hostApplicationLifetime,
                     SocketConnectionContextFactory connectionFactory,
                    ILogger<StreamHub> logger) : Hub
    {
        // 双向流方法：接收 clientStream，返回 IAsyncEnumerable
        public async IAsyncEnumerable<string> EchoStream(
            string host, string port,
            IAsyncEnumerable<string> clientStream,
            [EnumeratorCancellation] CancellationToken cancellationToken)
        {
            using var cancellationSource = CancellationTokenSource.CreateLinkedTokenSource(
                        cancellationToken, hostApplicationLifetime.ApplicationStopping);
            cancellationSource.CancelAfter(TimeSpan.FromHours(1));
            var cancellationTokenEx = cancellationSource.Token;

            string connectionId = Guid.CreateVersion7().ToString("N");

            if (logger.IsEnabled(LogLevel.Information))
            {
                logger.LogInformation("开始连接目标服务器 {host}:{port}，ConnectionId:{connectionId}", host, port, connectionId);
            }

            await using TcpConnectTargetServerService tcpConnectTargetServerService = new(connectionFactory, host, int.Parse(port));
            await tcpConnectTargetServerService.ConnectAsync(cancellationTokenEx);

            //发送id，表示连接成功
            yield return $"id:{connectionId}";

            //读取客户端数据
            var taskClient = HandlerClientAsync(tcpConnectTargetServerService, clientStream, cancellationTokenEx);

            //读取目标服务器数据
            await foreach (var item in tcpConnectTargetServerService.ReceiveAsync(cancellationTokenEx))
            {
                yield return $"data:{Convert.ToBase64String(item.Span)}";
            }

            await taskClient;
        }

        private static async Task HandlerClientAsync(TcpConnectTargetServerService tcpConnectTargetServerService,
         IAsyncEnumerable<string> clientStream, CancellationToken cancellationToken)
        {
            //读取客户端数据
            await foreach (var item in clientStream.WithCancellation(cancellationToken))
            {
                await tcpConnectTargetServerService.SendAsync(Convert.FromBase64String(item), cancellationToken);
            }
        }
    }
}