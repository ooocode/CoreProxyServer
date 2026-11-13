using CoreProxy.Server.Orleans.Internal;
using CoreProxy.Server.Orleans.Models;
using CoreProxy.Server.Orleans.Services;
using Microsoft.AspNetCore.SignalR;
using Microsoft.Extensions.DependencyInjection;

namespace CoreProxy.Server.Orleans.Endpoints
{
    internal static class AdminEndpoints
    {
        public static void Map(WebApplication app, CertificatePassword password, TimeSpan invokeTimeout)
        {
            // 管理页首页：/admin → wwwroot/admin.html（UseStaticFiles 不做无扩展名映射，故显式指向文件；
            // 鉴权已由 Program.cs 的中间件按 /admin 前缀放行前校验）
            app.MapGet("/admin", (HttpContext ctx) =>
            {
                // WebRootPath 在 wwwroot 不存在时为 null，直接 Path.Combine 会抛 ArgumentNullException → 500
                var env = ctx.RequestServices.GetRequiredService<IWebHostEnvironment>();
                var webRoot = env.WebRootPath ?? Path.Combine(env.ContentRootPath, "wwwroot");
                return Results.File(Path.Combine(webRoot, "admin.html"), "text/html");
            });

            // 管理页数据：返回当前 gRPC 隧道清单（鉴权见 Program.cs 的中间件）
            app.MapGet("/api/status", (HttpContext ctx, TunnelRegistry tunnels) =>
                AdminApi.HandleStatus(ctx, password.Password, tunnels));

            // 在线 SignalR 设备及其上报的可调用方法（鉴权同上）
            app.MapGet("/api/devices", (HttpContext ctx, DeviceRegistry devices) =>
                AdminApi.HandleDevices(ctx, password.Password, devices));

            // 调用 SignalR 在线设备上报的某个方法并等返回值（鉴权同上）：
            // 带 deviceId 精确指定设备；不带 deviceId 则从所有上报了该方法的在线设备中随机选一个。
            // 请求体 InvokeRequest 由 minimal API 强类型绑定（source-gen，见 ConfigureHttpJsonOptions）
            app.MapPost("/api/invoke", (InvokeRequest request, HttpContext ctx,
                IHubContext<ChatHub> hubContext, DeviceRegistry devices) =>
                AdminApi.HandleInvokeAsync(
                    request, password.Password, ctx, hubContext, devices,
                    defaultInvokeTimeout: invokeTimeout));

            // 生成加密的反代地址（鉴权同上）：输入目标地址，返回 token 与代理路径。
            // 只有服务端能签发 token，外部无法自行拼出指向其它目标的代理链接。
            app.MapPost("/api/proxy-link", (ProxyLinkRequest request, HttpContext ctx) =>
                AdminApi.HandleProxyLink(request, password.Password, ctx));
        }
    }
}
