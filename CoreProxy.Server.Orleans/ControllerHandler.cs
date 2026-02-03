using System.Runtime.CompilerServices;
using System.Threading.Channels;
using CoreProxy.Server.Orleans.Internal;
using Google.Protobuf;
using Hello;
using Microsoft.AspNetCore.Server.Kestrel.Transport.Sockets;

namespace CoreProxy.Server.Orleans
{
    public class ControllerHandler
    {
        private readonly static System.Collections.Concurrent.ConcurrentDictionary<string, Channel<HttpData>> ClientChannels = new();

        public static void MapSSE(WebApplication webApplication)
        {
            webApplication.MapGet("/sse", (
                IHostApplicationLifetime hostApplicationLifetime,
                 SocketConnectionContextFactory connectionFactory,
                ILogger<ControllerHandler> logger, string host, int port,
                CancellationToken cancellationToken) =>
            {
                using var cancellationSource = CancellationTokenSource.CreateLinkedTokenSource(
                    cancellationToken, hostApplicationLifetime.ApplicationStopping);
                cancellationSource.CancelAfter(TimeSpan.FromHours(1));

                return TypedResults.ServerSentEvents(GetStrings(connectionFactory, logger, host, port, cancellationSource.Token));
            });

          /*   webApplication.MapPost("/send/{cid}", async (string cid, HttpData data, CancellationToken cancellationToken) =>
            {
                if (ClientChannels.TryGetValue(cid, out var channel))
                {
                    await channel.Writer.WriteAsync(data, cancellationToken);
                    return Results.Ok();
                }
                return Results.NotFound();
            }); */
        }

        private static async IAsyncEnumerable<string> GetStrings(SocketConnectionContextFactory connectionFactory,
            ILogger logger, string host, int port,
            [EnumeratorCancellation] CancellationToken cancellationToken)
        {
            string connectionId = Guid.CreateVersion7().ToString("N");

            try
            {
                if (logger.IsEnabled(LogLevel.Information))
                {
                    logger.LogInformation("开始连接目标服务器 {host}:{port}，ConnectionId:{connectionId}", host, port, connectionId);
                }

                await using TcpConnectTargetServerService tcpConnectTargetServerService = new(connectionFactory, host, port);
                await tcpConnectTargetServerService.ConnectAsync(cancellationToken);

                var clientChannel = ClientChannels.GetOrAdd(connectionId,
                Channel.CreateBounded<HttpData>(new BoundedChannelOptions(10)
                {
                    FullMode = BoundedChannelFullMode.Wait,
                }));

                //发送id，表示连接成功
                yield return $"id:{connectionId}";

                //读取客户端数据
                var taskClient = HandlerClientAsync(tcpConnectTargetServerService, clientChannel, cancellationToken);

                //读取目标服务器数据
                await foreach (var item in tcpConnectTargetServerService.ReceiveAsync(cancellationToken))
                {
                    HttpData httpData = new()
                    {
                        Payload = UnsafeByteOperations.UnsafeWrap(item),
                        Crc32 = System.IO.Hashing.Crc32.HashToUInt32(item.Span),
                    };
                    yield return $"data:{httpData.ToByteString().ToBase64()}";
                }

                await taskClient;
            }
            finally
            {
                if (ClientChannels.TryRemove(connectionId, out var channel))
                {
                    channel.Writer.TryComplete();
                }
            }
        }

        private static async Task HandlerClientAsync(TcpConnectTargetServerService tcpConnectTargetServerService,
             Channel<HttpData> requestStream, CancellationToken cancellationToken)
        {
            //读取客户端数据
            await foreach (var item in requestStream.Reader.ReadAllAsync(cancellationToken))
            {
                await tcpConnectTargetServerService.SendAsync(item.Payload.Memory, cancellationToken);
            }
        }
    }
}