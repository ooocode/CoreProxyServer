using CoreProxy.Server.Orleans.Internal;
using CoreProxy.Server.Orleans.Models;
using DotNext.IO.Pipelines;
using Grpc.Core;
using Hello;
using Microsoft.AspNetCore.Connections;
using Microsoft.AspNetCore.SignalR;
using ServerWebApplication.Common;
using System.Runtime.CompilerServices;

namespace CoreProxy.Server.Orleans.Services
{
    public class ChatHub : Hub
    {
        private readonly ILogger<MyGrpcService> logger;
        private readonly IHostApplicationLifetime hostApplicationLifetime;
        private readonly IConnectionFactory connectionFactory;

        public ChatHub(
            ILogger<MyGrpcService> logger,
            IHostApplicationLifetime hostApplicationLifetime,
            IConnectionFactory connectionFactory,
            CertificatePassword certificatePassword)
        {
            this.logger = logger;
            this.hostApplicationLifetime = hostApplicationLifetime;
            this.connectionFactory = connectionFactory;
        }

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


        public async IAsyncEnumerable<string> Connect(
            ConnectRequest request,
            [EnumeratorCancellation] CancellationToken ctx)
        {
            using var cancellationSource = CancellationTokenSource.CreateLinkedTokenSource(
                        ctx, hostApplicationLifetime.ApplicationStopping);
            var cancellationToken = cancellationSource.Token;

            var endpoint = await DnsService.GetIpEndpointAsync(request.Host, request.Port, cancellationToken);
            await using var connectionContext = await connectionFactory.ConnectAsync(endpoint, cancellationToken);

            Context.Items[nameof(ConnectionContext)] = connectionContext;

            var connectItem = new ConnectItem
            {
                ClientIpAddress = this.Context.GetHttpContext()?.Connection.RemoteIpAddress?.ToString() ?? string.Empty,
                ConnectionContext = null,
                DateTime = DateTimeOffset.Now
            };

            string sessionId = Context.ConnectionId;
            GlobalState.Sockets.TryAdd(sessionId, connectItem);

            //发送包，表示连接成功
            yield return sessionId;


            //读取目标服务器数据
            await foreach (var item in connectionContext.Transport.Input.ReadAllAsync(cancellationToken))
            {
                yield return Convert.ToBase64String(item.Span);
            }
        }

        public async Task SendToServer(string body)
        {
            var connectionContext = Context.Items[nameof(ConnectionContext)] as ConnectionContext;
            ArgumentNullException.ThrowIfNull(connectionContext, "ConnectionContext is not initialized. Please call Connect method first.");
            var bytes = Convert.FromBase64String(body);
            await connectionContext.Transport.Output.WriteAsync(bytes, Context.ConnectionAborted);
        }
    }
}
