using CoreProxy.Server.Orleans.Internal;
using Microsoft.AspNetCore.Server.Kestrel.Transport.Sockets;
using Microsoft.AspNetCore.SignalR;

namespace CoreProxy.Server.Orleans.Services
{
    public class ChatHub(
        ILogger<MyGrpcService> logger,
        IHostApplicationLifetime hostApplicationLifetime,
        SocketConnectionContextFactory connectionFactory) : Hub
    {
        public override Task OnConnectedAsync()
        {
            string sessionId = Context.ConnectionId;
            logger.LogInformation($"Client connected: {sessionId} 总连接数{GlobalState.Sockets.Count}");
            return base.OnConnectedAsync();
        }

        public override async Task OnDisconnectedAsync(Exception? exception)
        {
            string sessionId = Context.ConnectionId;
            GlobalState.Sockets.TryRemove(sessionId, out var _);
            logger.LogError($"Client disconnected: {sessionId} 总连接数{GlobalState.Sockets.Count}");
            await base.OnDisconnectedAsync(exception);
        }

        public void ShowXX()
        {

        }



        //public async IAsyncEnumerable<string> Connect(
        //    ConnectRequest request,
        //    [EnumeratorCancellation] CancellationToken ctx)
        //{
        //    using var cancellationSource = CancellationTokenSource.CreateLinkedTokenSource(
        //                ctx, hostApplicationLifetime.ApplicationStopping);
        //    var cancellationToken = cancellationSource.Token;

        //    var iPAddresses = await DnsService.GetIpAddressesAsync(request.Host, cancellationToken);
        //    if (iPAddresses == null || iPAddresses.Length == 0)
        //    {
        //        throw new RpcException(new Status(StatusCode.InvalidArgument, $"Host[{request.Host}] not found"));
        //    }

        //    using var socket = new Socket(SocketType.Stream, ProtocolType.Tcp)
        //    {
        //        NoDelay = true
        //    };

        //    await socket.ConnectAsync(iPAddresses, request.Port, CancellationToken.None);
        //    await using var connectionContext = connectionFactory.Create(socket);

        //    Context.Items[nameof(ConnectionContext)] = connectionContext;

        //    var connectItem = new ConnectItem
        //    {
        //        ClientIpAddress = this.Context.GetHttpContext()?.Connection.RemoteIpAddress?.ToString() ?? string.Empty,
        //        ConnectionContext = null,
        //        DateTime = DateTimeOffset.Now
        //    };

        //    string sessionId = Context.ConnectionId;
        //    GlobalState.Sockets.TryAdd(sessionId, connectItem);

        //    //发送包，表示连接成功
        //    yield return sessionId;


        //    //读取目标服务器数据
        //    await foreach (var item in connectionContext.Transport.Input.ReadAllAsync(cancellationToken))
        //    {
        //        yield return Convert.ToBase64String(item.Span);
        //    }
        //}

        //public async Task SendToServer(string body)
        //{
        //    var connectionContext = Context.Items[nameof(ConnectionContext)] as ConnectionContext;
        //    ArgumentNullException.ThrowIfNull(connectionContext, "ConnectionContext is not initialized. Please call Connect method first.");
        //    var bytes = Convert.FromBase64String(body);
        //    await connectionContext.Transport.Output.WriteAsync(bytes, Context.ConnectionAborted);
        //}
    }
}
