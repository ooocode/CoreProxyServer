using CoreProxy.Server.Orleans.Internal;
using Hello;
using Microsoft.AspNetCore.SignalR;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace CoreProxy.Server.Orleans.Services
{
    /// <summary>/api/status 的响应体。camelCase 由 AdminJsonContext 的命名策略决定。</summary>
    public sealed record StatusPayload(int Total, IReadOnlyList<RuntimeTunnel> Items);

    /// <summary>/api/devices 里的一台在线 SignalR 设备（含它主动上报的可调用方法）。</summary>
    public sealed record DevicePayload(string DeviceId, string MachineName, string UserName, string ConnectionId, IReadOnlyList<string> Methods);

    /// <summary>/api/devices 的响应体。</summary>
    public sealed record DevicesPayload(int Total, IReadOnlyList<DevicePayload> Items);

    /// <summary>/api/invoke 的请求体：deviceId 指定目标在线设备，method 指定调用它上报的哪个方法，payload 原样传给该方法。</summary>
    public sealed record InvokeRequest(string DeviceId, string Method, string? Payload);

    /// <summary>/api/invoke 的响应体：result 是客户端方法的返回值原样透传。</summary>
    public sealed record InvokePayload(string DeviceId, string ConnectionId, string Method, string Result);

    /// <summary>
    /// 管理接口专用的 source-gen JSON 上下文（AOT）。
    /// 刻意与 AppJsonSerializerContext 分开：后者的 Options 被 SignalR 的 JSON 协议共用
    /// （Program.cs 里 AddJsonProtocol），改它的命名策略会改掉 SignalR 的线上格式。
    /// </summary>
    [JsonSourceGenerationOptions(PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase)]
    [JsonSerializable(typeof(StatusPayload))]
    [JsonSerializable(typeof(DevicesPayload))]
    [JsonSerializable(typeof(InvokeRequest))]
    [JsonSerializable(typeof(InvokePayload))]
    public partial class AdminJsonContext : JsonSerializerContext;

    /// <summary>
    /// 管理页（/admin 静态文件 + /api/status 数据）的服务端入口。
    /// 鉴权用 HTTP Basic 复用 gRPC 的同一把口令；与 MyGrpcService.CheckPassword 不同的是，
    /// 这里**不做回环豁免** —— 本机访问同样要求凭据。
    /// </summary>
    public static class AdminApi
    {
        private const string BasicPrefix = "Basic ";

        /// <summary>
        /// 等客户端返回值的上限。SignalR 的 InvokeAsync 本身不设超时，
        /// 客户端既不返回也不断开时会一直挂着，故由服务端这边兜底。
        /// </summary>
        private static readonly TimeSpan InvokeTimeout = TimeSpan.FromSeconds(30);

        /// <summary>
        /// /api/invoke 的端上调用抽象：(connectionId, method, payload, ct) → 客户端返回值。
        /// 抽成方法参数而非直连 SignalR，是为了让"返回值透传 / 端上失败"两条分支可被单测覆盖
        /// —— InvokeAsync&lt;string&gt; 的返回值依赖 SignalR 协议层握手，假 IHubContext 复现不出来。
        /// 生产调用方不传，走默认的真实实现。
        /// </summary>
        public delegate Task<string> InvokeOnClient(string connectionId, string method, string payload, CancellationToken cancellationToken);

        /// <summary>
        /// 判定某请求路径是否属于受保护的管理面。
        /// 注意 /admin 与 /admin.html 是两个不同的路径段：StartsWithSegments("/admin") 匹配不到
        /// /admin.html，漏掉它会让静态页面绕过鉴权被直接访问，故两个前缀都要显式列出。
        /// API 路径同样用 StartsWithSegments：既容忍尾斜杠，又不会误匹配 /api/statusx 这类同前缀路径。
        /// </summary>
        public static bool RequiresAuth(PathString path) =>
            path.StartsWithSegments("/admin")
            || path.StartsWithSegments("/admin.html")
            || path.StartsWithSegments("/api/status")
            || path.StartsWithSegments("/api/devices")
            || path.StartsWithSegments("/api/invoke");

        /// <summary>
        /// 校验 HTTP Basic 凭据。口令取解码后 ":" 之后那一段（无 ":" 则取整段），
        /// 用常数时间比较避免时序泄漏。缺头、scheme 不符、base64 非法一律返回 false 且不抛异常。
        /// </summary>
        public static bool CheckBasicAuth(HttpContext ctx, string password)
        {
            var header = ctx.Request.Headers.Authorization.ToString();
            if (!header.StartsWith(BasicPrefix, StringComparison.Ordinal))
            {
                return false;
            }

            byte[] raw;
            try
            {
                raw = Convert.FromBase64String(header[BasicPrefix.Length..].Trim());
            }
            catch (FormatException)
            {
                return false;
            }

            var decoded = Encoding.UTF8.GetString(raw);
            var separator = decoded.IndexOf(':');
            var candidate = separator >= 0 ? decoded[(separator + 1)..] : decoded;

            return CryptographicOperations.FixedTimeEquals(
                Encoding.UTF8.GetBytes(candidate),
                Encoding.UTF8.GetBytes(password));
        }

        /// <summary>
        /// 处理 GET /api/status：未带凭据返回 401 + WWW-Authenticate；否则返回当前 gRPC 隧道清单 JSON。
        /// </summary>
        public static IResult HandleStatus(HttpContext ctx, string password)
        {
            if (!CheckBasicAuth(ctx, password))
            {
                ctx.Response.Headers.WWWAuthenticate = "Basic realm=\"admin\"";
                return Results.StatusCode(StatusCodes.Status401Unauthorized);
            }

            var items = ConnectionRepository.GetGrpcTunnels();
            return Results.Json(
                new StatusPayload(items.Count, items),
                AdminJsonContext.Default.StatusPayload);
        }

        /// <summary>
        /// 处理 GET /api/devices：返回当前在线的 SignalR 设备及其【主动上报】的可调用方法，
        /// 供 admin 页展示方法清单、并驱动 /api/invoke 的调用面板。按 DeviceId 排序保证输出稳定。
        /// </summary>
        public static IResult HandleDevices(HttpContext ctx, string password)
        {
            if (!CheckBasicAuth(ctx, password))
            {
                ctx.Response.Headers.WWWAuthenticate = "Basic realm=\"admin\"";
                return Results.StatusCode(StatusCodes.Status401Unauthorized);
            }

            var items = GloableSessionsManager.SignalrOnlineClients.Values
                .OrderBy(c => c.DeviceId, StringComparer.Ordinal)
                .Select(c => new DevicePayload(
                    c.DeviceId, c.MachineName, c.UserName, c.ConnectionId, c.Methods.ToArray()))
                .ToList();

            return Results.Json(
                new DevicesPayload(items.Count, items),
                AdminJsonContext.Default.DevicesPayload);
        }

        /// <summary>
        /// 处理 POST /api/invoke：按 deviceId 找到在线设备，校验它【主动上报】的方法列表里含请求的 method，
        /// 再经 SignalR 调它的该 method（单个 string 参数），等返回值原样透传回 HTTP。
        /// 返回值里带 connectionId，可区分同名设备多次上线（protocol 里 DeviceId 非唯一键，重复上线会被拒绝，
        /// 这里回传实际生效的 ConnectionId 作为凭证）。
        /// 请求体由 minimal API 强类型绑定为 <see cref="InvokeRequest"/>（AOT 走 AdminJsonContext 的 source-gen）；
        /// JSON 非法由绑定层直接 400，缺字段则在下面补一次校验。
        /// 语义分层：401 未鉴权 / 400 缺 deviceId 或 method / 404 设备不在线 /
        /// 409 设备在线但未上报该方法（拒绝调用它没声明支持的方法）/ 502 端上调用失败（客户端断开、未实现、超时）。
        /// invokeOnClient 仅供测试注入；生产调用方不传，默认走真实 SignalR。
        /// </summary>
        public static async Task<IResult> HandleInvokeAsync(InvokeRequest request, string password, HttpContext ctx,
            IHubContext<ChatHub> hubContext, InvokeOnClient? invokeOnClient = null)
        {
            if (!CheckBasicAuth(ctx, password))
            {
                ctx.Response.Headers.WWWAuthenticate = "Basic realm=\"admin\"";
                return Results.StatusCode(StatusCodes.Status401Unauthorized);
            }

            if (string.IsNullOrWhiteSpace(request.DeviceId) || string.IsNullOrWhiteSpace(request.Method))
            {
                return Results.StatusCode(StatusCodes.Status400BadRequest);
            }

            if (!GloableSessionsManager.SignalrOnlineClients.TryGetValue(request.DeviceId, out var client))
            {
                return Results.StatusCode(StatusCodes.Status404NotFound);
            }

            // 只允许调用设备【主动上报】的方法：没上报就拒绝，避免把任意方法名透传给设备。
            // 大小写不敏感匹配，但【用设备上报的精确拼写】下发——SignalR 客户端 On 处理器按精确
            // 方法名注册，若用请求原文下发会因大小写不符而匹配不到处理器，造出"过了白名单却 502"的假象。
            var matchedMethod = client.Methods.FirstOrDefault(m =>
                string.Equals(m, request.Method, StringComparison.OrdinalIgnoreCase));
            if (matchedMethod is null)
            {
                return Results.StatusCode(StatusCodes.Status409Conflict);
            }

            invokeOnClient ??= (connectionId, method, payload, ct) =>
                hubContext.Clients.Client(connectionId).InvokeAsync<string>(method, payload, ct);

            string result;
            try
            {
                result = await invokeOnClient(client.ConnectionId, matchedMethod, request.Payload ?? string.Empty, ctx.RequestAborted)
                    .WaitAsync(InvokeTimeout, ctx.RequestAborted);
            }
            catch (Exception)
            {
                // 设备断线、客户端没实现该方法、或等返回值超时，都归为端上失败。
                return Results.StatusCode(StatusCodes.Status502BadGateway);
            }

            return Results.Json(
                new InvokePayload(request.DeviceId, client.ConnectionId, matchedMethod, result),
                AdminJsonContext.Default.InvokePayload);
        }
    }
}
