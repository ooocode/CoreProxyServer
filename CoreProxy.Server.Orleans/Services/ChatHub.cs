using Google.Protobuf;
using Hello;
using Microsoft.AspNetCore.SignalR;

namespace CoreProxy.Server.Orleans.Services
{
    public class ChatHub(ILogger<MyGrpcService> logger) : Hub
    {
        public override Task OnConnectedAsync()
        {
            string? info = Context.GetHttpContext()?.Request.Query["info"].ToString().Trim();
            if (string.IsNullOrWhiteSpace(info))
            {
                throw new Exception("缺少info");
            }

            var client = SignalrOnlineClient.Parser.ParseFrom(ByteString.FromBase64(info));
            client.ConnectionId = Context.ConnectionId;

            if (!GloableSessionsManager.SignalrOnlineClients.TryAdd(client.DeviceId, client))
            {
                throw new Exception($"设备{client.DeviceId}重复上线");
            }

            if (logger.IsEnabled(LogLevel.Information))
            {
                logger.LogInformation("设备上线: {deviceId} {connectionId}", client.DeviceId, client.ConnectionId);
            }

            return base.OnConnectedAsync();
        }

        public override async Task OnDisconnectedAsync(Exception? exception)
        {
            string? info = Context.GetHttpContext()?.Request.Query["info"].ToString().Trim();
            if (!string.IsNullOrWhiteSpace(info))
            {
                var client = SignalrOnlineClient.Parser.ParseFrom(ByteString.FromBase64(info));
                if (GloableSessionsManager.SignalrOnlineClients.TryRemove(client.DeviceId, out var d))
                {
                    if (logger.IsEnabled(LogLevel.Error))
                    {
                        logger.LogError("设备离线: {d.DeviceId} {d.ConnectionId}", d.DeviceId, d.ConnectionId);
                    }
                }
            }

            await base.OnDisconnectedAsync(exception);
        }
    }
}
