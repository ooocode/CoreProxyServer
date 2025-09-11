using CoreProxy.Server.Orleans.Internal;
using Microsoft.AspNetCore.SignalR;

namespace CoreProxy.Server.Orleans.Services
{
    public class ChatHub(
        ILogger<MyGrpcService> logger) : Hub
    {
        public override Task OnConnectedAsync()
        {
            string sessionId = Context.ConnectionId;
            logger.LogInformation($"Client connected: {sessionId}");
            return base.OnConnectedAsync();
        }

        public override async Task OnDisconnectedAsync(Exception? exception)
        {
            string sessionId = Context.ConnectionId;
            logger.LogError($"Client disconnected: {sessionId} 总连接数{GlobalState.Sockets.Count}");
            await base.OnDisconnectedAsync(exception);
        }
    }
}
