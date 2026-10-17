using Hello;
using Microsoft.AspNetCore.SignalR;

namespace CoreProxy.Server.Orleans.Services
{
    /// <summary>
    /// 设备在线表维护。设备连上来时在 query 的 info 里带一段【明文 JSON】（见 DeviceInfo），
    /// 解析后映射成 proto SignalrOnlineClient 存入 GloableSessionsManager（gRPC GetSignalrOnlineClients 仍按该 proto 返回）。
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

            if (!GloableSessionsManager.SignalrOnlineClients.TryAdd(client.DeviceId, client))
            {
                throw new Exception($"设备{client.DeviceId}重复上线");
            }

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
            string? info = Context.GetHttpContext()?.Request.Query["info"].ToString().Trim();
            if (!string.IsNullOrWhiteSpace(info))
            {
                // 离线路径要尽量不抛：info 坏了也照常走 base，避免把 OnDisconnectedAsync 本身搞崩。
                string? deviceId = null;
                try
                {
                    deviceId = DeviceInfo.Parse(info).DeviceId;
                }
                catch (Exception ex)
                {
                    logger.LogWarning(ex, "设备离线时解析 info 失败，跳过在线表清理");
                }

                if (deviceId is not null && GloableSessionsManager.SignalrOnlineClients.TryRemove(deviceId, out var d))
                {
                    if (logger.IsEnabled(LogLevel.Error))
                    {
                        logger.LogError("设备离线: {d.DeviceId} {d.ConnectionId}", d.DeviceId, d.ConnectionId);
                    }
                }
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
