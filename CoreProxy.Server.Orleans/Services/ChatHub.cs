using CoreProxy.Server.Orleans.Internal;
using Microsoft.AspNetCore.SignalR;

namespace CoreProxy.Server.Orleans.Services
{
    public class ChatHub(
        ILogger<MyGrpcService> logger) : Hub
    {
        public static System.Collections.Concurrent.ConcurrentDictionary<string, string> OnlineClients = [];

        public override Task OnConnectedAsync()
        {
            string sessionId = Context.ConnectionId;
            OnlineClients.TryAdd(sessionId, sessionId);

            logger.LogInformation($"Client connected: {sessionId}");
            return base.OnConnectedAsync();
        }

        public override async Task OnDisconnectedAsync(Exception? exception)
        {
            string sessionId = Context.ConnectionId;
            OnlineClients.TryRemove(sessionId, out var _);

            logger.LogError($"Client disconnected: {sessionId} 总连接数{GlobalState.Sockets.Count}");
            await base.OnDisconnectedAsync(exception);
        }
    }
}
