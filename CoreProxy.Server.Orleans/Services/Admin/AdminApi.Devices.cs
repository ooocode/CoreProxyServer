using CoreProxy.Server.Orleans.Internal;
using Microsoft.AspNetCore.Http;

namespace CoreProxy.Server.Orleans.Services
{
    public static partial class AdminApi
    {
        /// <summary>
        /// 处理 GET /api/devices：返回当前在线的 SignalR 客户端及其【主动上报】的可调用方法，
        /// 供 admin 页展示方法清单、并驱动 /api/invoke 的调用面板。
        /// 【每条连接一行】：同一个 DeviceId 允许多条连接同时在线，因此 total 数的是连接数而非设备数；
        /// 按 DeviceId + ConnectionId 排序，保证输出稳定且同 ID 的多条连接可区分。
        /// </summary>
        public static IResult HandleDevices(HttpContext ctx, string password, DeviceRegistry devices)
        {
            if (RejectUnauthorized(ctx, password) is { } rejection)
            {
                return rejection;
            }

            var items = devices.Snapshot()
                .OrderBy(c => c.DeviceId, StringComparer.Ordinal)
                .ThenBy(c => c.ConnectionId, StringComparer.Ordinal)
                .Select(c => new DevicePayload(
                    c.DeviceId, c.MachineName, c.UserName, c.ConnectionId, c.Methods.ToArray()))
                .ToList();

            return Results.Json(
                new DevicesPayload(items.Count, items),
                AdminJsonContext.Default.DevicesPayload);
        }
    }
}
