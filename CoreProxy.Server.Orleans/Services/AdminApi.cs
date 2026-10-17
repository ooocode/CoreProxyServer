using CoreProxy.Server.Orleans.Internal;
using Hello;
using Microsoft.AspNetCore.SignalR;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json.Serialization;

namespace CoreProxy.Server.Orleans.Services
{
    /// <summary>/api/status 的响应体。camelCase 由 AdminJsonContext 的命名策略决定。</summary>
    public sealed record StatusPayload(int Total, IReadOnlyList<RuntimeTunnel> Items);

    /// <summary>/api/devices 里的一台在线 SignalR 设备（含它主动上报的可调用方法）。</summary>
    public sealed record DevicePayload(string DeviceId, string MachineName, string UserName, string ConnectionId, IReadOnlyList<string> Methods);

    /// <summary>/api/devices 的响应体。</summary>
    public sealed record DevicesPayload(int Total, IReadOnlyList<DevicePayload> Items);

    /// <summary>
    /// /api/invoke 的请求体。method 指定要调用的方法，payload 原样传给该方法。
    /// deviceId 可选：传了则精确指定在线设备；不传（或空白）则在【所有上报了该方法的在线客户端】中随机选一个。
    /// </summary>
    public sealed record InvokeRequest(string? DeviceId, string Method, string? Payload);

    /// <summary>
    /// /api/invoke 的响应体：deviceId/connectionId 是【实际承接本次调用】的设备与连接，
    /// method 是实际下发给设备的精确拼写，result 是客户端方法的返回值原样透传。
    /// 同一个 deviceId 允许多条连接同时在线，connectionId 是区分它们的唯一凭证；
    /// 随机选择时回显的 deviceId 可能与请求里的不同（请求未指定时）——调用方应以此为准。
    /// </summary>
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
        /// 处理 GET /api/devices：返回当前在线的 SignalR 客户端及其【主动上报】的可调用方法，
        /// 供 admin 页展示方法清单、并驱动 /api/invoke 的调用面板。
        /// 【每条连接一行】：同一个 DeviceId 允许多条连接同时在线，因此 total 数的是连接数而非设备数；
        /// 按 DeviceId + ConnectionId 排序，保证输出稳定且同 ID 的多条连接可区分。
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
                .ThenBy(c => c.ConnectionId, StringComparer.Ordinal)
                .Select(c => new DevicePayload(
                    c.DeviceId, c.MachineName, c.UserName, c.ConnectionId, c.Methods.ToArray()))
                .ToList();

            return Results.Json(
                new DevicesPayload(items.Count, items),
                AdminJsonContext.Default.DevicesPayload);
        }

        /// <summary>
        /// 处理 POST /api/invoke。先确定候选连接：
        /// 1) 请求带 deviceId：只在 DeviceId 相同的在线连接里筛选；
        /// 2) 请求不带 deviceId：在所有在线连接里筛选。
        /// 候选 = 上报了 method 的连接（大小写不敏感匹配），再用 Random.Shared 均匀随机选一条。
        /// 同一个 DeviceId 允许多条连接同时在线，因此即便指定了 deviceId，也可能有多条候选。
        /// 选定后经 SignalR 调它的该 method（单个 string 参数），等返回值原样透传回 HTTP。
        /// 返回值回显【实际承接调用】的 deviceId/connectionId 与方法精确拼写——connectionId 是区分
        /// 同 DeviceId 多条连接的唯一凭证。
        /// 请求体由 minimal API 强类型绑定为 <see cref="InvokeRequest"/>（AOT 走 AdminJsonContext 的 source-gen）；
        /// JSON 非法由绑定层直接 400，缺字段则在下面补一次校验。
        /// 语义分层：401 未鉴权 / 400 缺 method / 404 指定的 deviceId 没有在线连接、或全局模式下无连接支持该方法 /
        /// 409 指定的 deviceId 有在线连接但没有一条上报该方法 / 502 端上调用失败（客户端断开、未实现、超时）。
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

            if (string.IsNullOrWhiteSpace(request.Method))
            {
                return Results.StatusCode(StatusCodes.Status400BadRequest);
            }

            // 空白 deviceId 等价于不传（全局随机）；否则只在同 DeviceId 的连接里选。
            var pinnedDeviceId = string.IsNullOrWhiteSpace(request.DeviceId) ? null : request.DeviceId;

            // 先做全量快照再选。ConcurrentDictionary.Values 的枚举是线程安全的，
            // 期间设备上下线不会让枚举抛异常，最坏只是把已断开的连接纳入候选（调用时归为 502）。
            var candidates = new List<(SignalrOnlineClient Client, string Method)>();
            var pinnedDeviceOnline = false;
            foreach (var candidate in GloableSessionsManager.SignalrOnlineClients.Values)
            {
                if (pinnedDeviceId is not null)
                {
                    if (!string.Equals(candidate.DeviceId, pinnedDeviceId, StringComparison.Ordinal))
                    {
                        continue;
                    }
                    pinnedDeviceOnline = true;
                }

                // 只允许调用设备【主动上报】的方法：没上报就排除，避免把任意方法名透传给设备。
                var method = FindReportedMethod(candidate, request.Method);
                if (method is not null)
                {
                    candidates.Add((candidate, method));
                }
            }

            if (candidates.Count == 0)
            {
                // 指定了 deviceId：同 ID 有连接但都不支持该方法 → 409；同 ID 完全没有连接 → 404。
                // 未指定 deviceId：没有任何在线连接支持该方法 → 404。
                return Results.StatusCode(pinnedDeviceId is not null && pinnedDeviceOnline
                    ? StatusCodes.Status409Conflict
                    : StatusCodes.Status404NotFound);
            }

            var (client, matchedMethod) = candidates[Random.Shared.Next(candidates.Count)];

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
                new InvokePayload(client.DeviceId, client.ConnectionId, matchedMethod, result),
                AdminJsonContext.Default.InvokePayload);
        }

        /// <summary>
        /// 在设备主动上报的方法列表里做大小写不敏感匹配，返回【设备上报的精确拼写】。
        /// 下发必须用精确拼写：SignalR 客户端 On 处理器按精确方法名注册，用请求原文下发会因大小写
        /// 不符而匹配不到处理器，造出"过了白名单却 502"的假象。未上报则返回 null。
        /// </summary>
        private static string? FindReportedMethod(SignalrOnlineClient client, string method) =>
            client.Methods.FirstOrDefault(m =>
                string.Equals(m, method, StringComparison.OrdinalIgnoreCase));
    }
}
