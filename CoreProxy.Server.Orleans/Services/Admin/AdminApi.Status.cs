using CoreProxy.Server.Orleans.Internal;
using Microsoft.AspNetCore.Http;

namespace CoreProxy.Server.Orleans.Services
{
    public static partial class AdminApi
    {
        /// <summary>
        /// 处理 GET /api/status：未带凭据返回 401 + WWW-Authenticate；否则返回当前 gRPC 隧道清单 JSON。
        /// </summary>
        public static IResult HandleStatus(HttpContext ctx, string password, TunnelRegistry tunnels)
        {
            if (RejectUnauthorized(ctx, password) is { } rejection)
            {
                return rejection;
            }

            var items = tunnels.GetGrpcTunnels();
            return Results.Json(
                new StatusPayload(items.Count, items),
                AdminJsonContext.Default.StatusPayload);
        }
    }
}
