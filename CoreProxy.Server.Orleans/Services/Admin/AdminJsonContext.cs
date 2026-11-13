using System.Text.Json.Serialization;

namespace CoreProxy.Server.Orleans.Services
{
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
    [JsonSerializable(typeof(ProxyLinkRequest))]
    [JsonSerializable(typeof(ProxyLinkPayload))]
    public partial class AdminJsonContext : JsonSerializerContext;
}
