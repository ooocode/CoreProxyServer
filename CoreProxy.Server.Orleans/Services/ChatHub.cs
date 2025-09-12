using Microsoft.AspNetCore.SignalR;

namespace CoreProxy.Server.Orleans.Services
{
    public class ChatHub(ILogger<MyGrpcService> logger) : Hub
    {
        public override Task OnConnectedAsync()
        {
            string sessionId = Context.ConnectionId;
            string? userName = Context.GetHttpContext()?.Request.Query["username"].ToString().Trim();
            if (string.IsNullOrWhiteSpace(userName))
            {
                throw new Exception("缺少username");
            }

            if (!GloableSessionsManager.SignalrOnlineClients.TryAdd(userName, sessionId))
            {
                throw new Exception($"用户{userName}重复上线");
            }

            logger.LogInformation($"用户上线: {userName} {sessionId}");
            return base.OnConnectedAsync();
        }

        public override async Task OnDisconnectedAsync(Exception? exception)
        {
            string? userName = Context.GetHttpContext()?.Request.Query["username"].ToString().Trim();
            if (!string.IsNullOrWhiteSpace(userName) && GloableSessionsManager.SignalrOnlineClients.TryRemove(userName, out var sessionId))
            {
                logger.LogError($"用户离线: {userName} {sessionId}");
            }

            await base.OnDisconnectedAsync(exception);
        }
    }
}
