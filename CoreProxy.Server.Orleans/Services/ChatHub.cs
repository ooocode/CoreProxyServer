using Hello;
using Microsoft.AspNetCore.SignalR;

namespace CoreProxy.Server.Orleans.Services
{
    /// <summary>
    /// 设备在线表维护。设备连上来时在 query 的 info 里带一段【明文 JSON】（见 DeviceInfo），
    /// 解析后映射成 proto SignalrOnlineClient 存入 GloableSessionsManager（gRPC GetSignalrOnlineClients 仍按该 proto 返回）。
    /// 在线表以 ConnectionId 为键：同一个 DeviceId 允许多条连接同时在线，互不覆盖。
    /// </summary>
    public class ChatHub(ILogger<MyGrpcService> logger) : Hub
    {
        public override Task OnConnectedAsync()
        {
            var device = DeviceInfo.Parse(RequireInfo());

            var client = new SignalrOnlineClient
            {
                DeviceId = device.DeviceId,
                MachineName = device.MachineName ?? string.Empty,
                UserName = device.UserName ?? string.Empty,
                ConnectionId = Context.ConnectionId,
            };
            if (device.Methods is { Count: > 0 })
            {
                client.Methods.AddRange(device.Methods);
            }

            // ConnectionId 由 SignalR 为每条连接唯一分配，因此同一 DeviceId 重复上线不会冲突。
            GloableSessionsManager.SignalrOnlineClients.TryAdd(client.ConnectionId, client);

            if (logger.IsEnabled(LogLevel.Information))
            {
                // 一并记下设备上报的可调用方法，便于排查 /api/invoke 的 409（未上报该方法）。
                logger.LogInformation("设备上线: {deviceId} {connectionId} 可调用方法[{methods}]",
                    client.DeviceId, client.ConnectionId, string.Join(",", client.Methods));
            }

            return base.OnConnectedAsync();
        }

        public override async Task OnDisconnectedAsync(Exception? exception)
        {
            // 只移除这条连接自己。同一 DeviceId 的其它在线连接不受影响。
            // 不再依赖 query 里的 info：即便断开时取不到/解析不了 info，也不会漏清理。
            if (GloableSessionsManager.SignalrOnlineClients.TryRemove(Context.ConnectionId, out var d)
                && logger.IsEnabled(LogLevel.Error))
            {
                logger.LogError("设备离线: {d.DeviceId} {d.ConnectionId}", d.DeviceId, d.ConnectionId);
            }

            await base.OnDisconnectedAsync(exception);
        }

        private string RequireInfo()
        {
            string? info = Context.GetHttpContext()?.Request.Query["info"].ToString().Trim();
            if (string.IsNullOrWhiteSpace(info))
            {
                throw new Exception("缺少info");
            }
            return info;
        }
    }
}
