using Hello;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace CoreProxy.Server.Orleans.Services
{
    /// <summary>
    /// 设备线连接的 info 参数载体（明文 JSON，非 protobuf）。设备连 /chathub 时在 query 的 info 里带上它。
    /// camelCase 序列化/反序列化走 <see cref="DeviceJsonContext"/> 的 source-gen（AOT 友好）。
    /// Methods 是设备【主动上报】的可被服务端调用（server→client）的方法名列表。
    /// </summary>
    public sealed record DeviceInfo(string DeviceId, string? MachineName, string? UserName, IReadOnlyList<string>? Methods)
    {
        /// <summary>
        /// 解析 info 的明文 JSON。非法 JSON 或缺 DeviceId 抛异常——与原 protobuf 解析失败的行为一致，
        /// 由 SignalR 把该连接拒掉，不会往在线表塞半条脏数据。
        /// </summary>
        public static DeviceInfo Parse(string info)
        {
            DeviceInfo? device;
            try
            {
                device = JsonSerializer.Deserialize(info, DeviceJsonContext.Default.DeviceInfo);
            }
            catch (JsonException)
            {
                throw new Exception("info不是合法JSON");
            }

            if (device is null || string.IsNullOrWhiteSpace(device.DeviceId))
            {
                throw new Exception("info缺少deviceId");
            }
            return device;
        }
    }

    /// <summary>
    /// 设备线 connection 的 source-gen JSON 上下文（AOT，独立于 AdminJsonContext / 公共 context，避免牵动其它 HTTP/SignalR 格式）。
    /// 开大小写不敏感：设备端用 camelCase 或 PascalCase 键名都能解析，少一个踩坑点。
    /// </summary>
    [JsonSourceGenerationOptions(PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase, PropertyNameCaseInsensitive = true)]
    [JsonSerializable(typeof(DeviceInfo))]
    public partial class DeviceJsonContext : JsonSerializerContext;

    public static class GloableSessionsManager
    {
        /// <summary>
        /// SignalR 在线客户端 [connectionId-SignalrOnlineClient]。
        /// 刻意用 ConnectionId 而不是 DeviceId 做键：同一个 DeviceId 允许多条连接同时在线
        /// （重连、同机多实例等），这样断开时只移除自己那条，不会误删同 ID 的其它连接。
        /// </summary>
        public static readonly System.Collections.Concurrent.ConcurrentDictionary<string, SignalrOnlineClient> SignalrOnlineClients = [];
    }
}
