using CoreProxy.Server.Orleans.Internal;

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
    /// timeoutSeconds 可选：本次等待设备返回值的上限（秒），必须大于 0，不设上限；省略时用服务端默认值 900 秒（15 分钟）。
    /// </summary>
    public sealed record InvokeRequest(string? DeviceId, string Method, string? Payload, int? TimeoutSeconds = null);

    /// <summary>
    /// /api/invoke 的响应体：deviceId/connectionId 是【实际承接本次调用】的设备与连接，
    /// method 是实际下发给设备的精确拼写，result 是客户端方法的返回值原样透传。
    /// 同一个 deviceId 允许多条连接同时在线，connectionId 是区分它们的唯一凭证；
    /// 随机选择时回显的 deviceId 可能与请求里的不同（请求未指定时）——调用方应以此为准。
    /// </summary>
    public sealed record InvokePayload(string DeviceId, string ConnectionId, string Method, string Result);

    /// <summary>/api/proxy-link 的请求体：url 为要代理的目标地址，如 http://10.240.4.68。</summary>
    public sealed record ProxyLinkRequest(string? Url);

    /// <summary>/api/proxy-link 的响应体：token 是加密后的路径段，path 是可直接拼接的代理路径。</summary>
    public sealed record ProxyLinkPayload(string Token, string Path);
}
