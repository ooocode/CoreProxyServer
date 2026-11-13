using CoreProxy.Server.Orleans.Internal;
using Hello;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.SignalR;

namespace CoreProxy.Server.Orleans.Services
{
    public static partial class AdminApi
    {
        /// <summary>
        /// /api/invoke 的端上调用抽象：(connectionId, method, payload, ct) → 客户端返回值。
        /// 抽成方法参数而非直连 SignalR，是为了让"返回值透传 / 端上失败"两条分支可被单测覆盖
        /// —— InvokeAsync&lt;string&gt; 的返回值依赖 SignalR 协议层握手，假 IHubContext 复现不出来。
        /// 生产调用方不传，走默认的真实实现。
        /// </summary>
        public delegate Task<string> InvokeOnClient(string connectionId, string method, string payload, CancellationToken cancellationToken);

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
        /// 语义分层：400 缺 method 或 timeoutSeconds ≤ 0 / 404 指定的 deviceId 没有在线连接、
        /// 或全局模式下无连接支持该方法 / 409 指定的 deviceId 有在线连接但没有一条上报该方法 /
        /// 502 端上调用失败（客户端断开、未实现、抛异常）/ 504 等待设备返回值超时。
        /// invokeOnClient / defaultInvokeTimeout 仅供测试注入；生产调用方不传，默认走真实 SignalR、超时 900s。
        /// </summary>
        public static async Task<IResult> HandleInvokeAsync(InvokeRequest request, string password, HttpContext ctx,
            IHubContext<ChatHub> hubContext, DeviceRegistry devices,
            InvokeOnClient? invokeOnClient = null, TimeSpan? defaultInvokeTimeout = null)
        {
            // /api/invoke 有意不做 Basic 鉴权，见 RequiresAuth 的说明。

            // 请求体 JSON 字面量为 null 时绑定层可能传入 null；非空参数在方法体里仍要防御，
            // 否则会以 NullReferenceException → 500 暴露，而不是明确的 400。
            if (request is null || string.IsNullOrWhiteSpace(request.Method))
            {
                return Results.StatusCode(StatusCodes.Status400BadRequest);
            }

            // timeoutSeconds 是调用方显式申请的等待窗口：只拒绝非正数（0/负数无法表达有效的等待窗口），
            // 不设上限 —— 需要等多久由调用方决定，服务端不做静默截断。
            if (request.TimeoutSeconds is int requestedSeconds && requestedSeconds <= 0)
            {
                return Results.StatusCode(StatusCodes.Status400BadRequest);
            }

            // 未指定 timeoutSeconds 时用服务端默认值（可由配置覆盖）；指定时以请求为准。
            var invokeTimeout = request.TimeoutSeconds is int timeoutSeconds
                ? TimeSpan.FromSeconds(timeoutSeconds)
                : defaultInvokeTimeout ?? DefaultInvokeTimeout;

            // 空白 deviceId 等价于不传（全局随机）；否则只在同 DeviceId 的连接里选。
            var pinnedDeviceId = string.IsNullOrWhiteSpace(request.DeviceId) ? null : request.DeviceId;

            // 单次枚举同时完成"筛选 + 均匀随机"：水塘抽样保证每条候选被选中的概率相同，
            // 且不需要为候选列表分配内存。ConcurrentDictionary.Values 的枚举是线程安全的，
            // 期间设备上下线不会让枚举抛异常，最坏只是把已断开的连接纳入候选（调用时归为 502）。
            SignalrOnlineClient? client = null;
            string? matchedMethod = null;
            var candidateCount = 0;
            var pinnedDeviceOnline = false;
            foreach (var candidate in devices.Snapshot())
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
                if (method is null)
                {
                    continue;
                }

                // 第 n 条候选以 1/n 的概率替换当前选中项；枚举结束时即得到均匀随机的一条。
                candidateCount++;
                if (Random.Shared.Next(candidateCount) == 0)
                {
                    client = candidate;
                    matchedMethod = method;
                }
            }

            if (client is null || matchedMethod is null)
            {
                // 指定了 deviceId：同 ID 有连接但都不支持该方法 → 409；同 ID 完全没有连接 → 404。
                // 未指定 deviceId：没有任何在线连接支持该方法 → 404。
                return Results.StatusCode(pinnedDeviceId is not null && pinnedDeviceOnline
                    ? StatusCodes.Status409Conflict
                    : StatusCodes.Status404NotFound);
            }

            invokeOnClient ??= (connectionId, method, payload, ct) =>
                hubContext.Clients.Client(connectionId).InvokeAsync<string>(method, payload, ct);

            // 超时 token 既约束 WaitAsync，也传给端上调用：WaitAsync 只让 HTTP 尽快返回，
            // 真正取消 SignalR 调用还要靠这个 token，否则设备一直不返回时调用会永久挂起。
            using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(ctx.RequestAborted);
            timeoutCts.CancelAfter(invokeTimeout < MaxTimerDelay ? invokeTimeout : MaxTimerDelay);

            string result;
            try
            {
                result = await invokeOnClient(
                        client.ConnectionId, matchedMethod, request.Payload ?? string.Empty, timeoutCts.Token)
                    .WaitAsync(timeoutCts.Token);
            }
            catch (Exception) when (timeoutCts.IsCancellationRequested && !ctx.RequestAborted.IsCancellationRequested)
            {
                // 只有服务端超时自身触发才归为 504；调用方主动断开时不算网关超时。
                return Results.StatusCode(StatusCodes.Status504GatewayTimeout);
            }
            catch (Exception)
            {
                // 设备断线、客户端没实现该方法、或 handler 抛异常，都归为端上调用失败。
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
        private static string? FindReportedMethod(SignalrOnlineClient client, string method)
        {
            var methods = client.Methods;
            for (var i = 0; i < methods.Count; i++)
            {
                var reported = methods[i];
                if (string.Equals(reported, method, StringComparison.OrdinalIgnoreCase))
                {
                    return reported;
                }
            }

            return null;
        }
    }
}
