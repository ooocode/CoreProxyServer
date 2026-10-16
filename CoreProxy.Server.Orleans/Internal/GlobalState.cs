using System.Collections.Concurrent;

namespace CoreProxy.Server.Orleans.Internal
{
    /// <summary>
    /// 隧道来源类型。三处写入方（gRPC 隧道 / SignalR EchoStream / HTTP /stream）
    /// 各自携带的字段不同，靠 Kind 区分，供管理页等只按 gRPC 过滤。
    /// </summary>
    public static class ConnectionKind
    {
        public const string Grpc = "grpc";
        public const string Signalr = "signalr";
        public const string Http = "http";
    }

    public static class GlobalState
    {
        public static readonly ConcurrentDictionary<string, ConnectItem> Connections = [];
    }

    public class ConnectItem
    {
        /// <summary>隧道来源类型，见 <see cref="ConnectionKind"/>。</summary>
        public string? Kind { get; set; }

        /// <summary>
        /// gRPC 侧原始的 context.Peer（形如 ipv4:127.0.0.1:54321，带 scheme 前缀和源端口）。
        /// 仅 gRPC 写入。用于让 ClientIpAddress 派生串对 gRPC 逐字节保真（见下）。
        /// </summary>
        public string? Peer { get; set; }

        /// <summary>对端 IP（剥掉前缀/端口后的纯 IP）。gRPC / SignalR 有，HTTP /stream 无。管理页展示用。</summary>
        public string? ClientIp { get; set; }

        /// <summary>目标地址 host:port。gRPC / HTTP /stream 有，SignalR 无。</summary>
        public string? Target { get; set; }

        public required DateTimeOffset DateTime { get; set; }

        /// <summary>
        /// 派生属性，把各方原始字段按拆分前的原样拼回，保证 gRPC GetStatus 输出、Exe 日志、
        /// 以及既有测试断言逐字节一致。三种组合对应三种写入方：
        /// gRPC（有 Peer）→ "{peer} {target}"；SignalR（只有 ClientIp）→ "{ip}"；HTTP（只有 Target）→ "{target}"。
        /// </summary>
        public string ClientIpAddress => !string.IsNullOrEmpty(Peer)
            ? $"{Peer} {Target}"
            : (ClientIp, Target) switch
            {
                (not null, not null) => $"{ClientIp} {Target}",
                _ => ClientIp ?? Target ?? string.Empty
            };
    }
}
