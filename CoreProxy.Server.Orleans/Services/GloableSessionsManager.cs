using Hello;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Threading.Channels;

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

    public class SessionInfo
    {
        /// <summary>
        /// 发起人
        /// </summary>
        public required string Creator { get; set; }

        public required Channel<ReadOnlyMemory<byte>> ChannelA { get; set; }

        public required Channel<ReadOnlyMemory<byte>> ChannelB { get; set; }
    }

    public static class GloableSessionsManager
    {
        public static readonly System.Collections.Concurrent.ConcurrentDictionary<string, SessionInfo> SessionList = [];

        /// <summary>
        /// signalr在线客户端 [deviceId-SignalrOnlineClient]
        /// </summary>
        public static readonly System.Collections.Concurrent.ConcurrentDictionary<string, SignalrOnlineClient> SignalrOnlineClients = [];
    }
}
