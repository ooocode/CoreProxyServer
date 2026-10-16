namespace CoreProxy.Server.Orleans.Internal
{
    /// <summary>
    /// 一条管理页上展示的 gRPC 隧道。ClientIp/Target 在 gRPC 写入时保证非空，
    /// 因此这里是普通 string 而非 string?。序列化走 source-gen（见 AdminJsonContext）。
    /// </summary>
    public sealed record RuntimeTunnel(string ClientIp, string Target, DateTimeOffset StartedAt);

    public static class ConnectionRepository
    {
        /// <summary>
        /// 只取 gRPC 隧道的当前连接记录，供管理页 /api/status 展示。
        /// SignalR EchoStream / HTTP /stream 的记录（缺目标或缺客户端 IP）一律不上榜。
        /// </summary>
        public static List<RuntimeTunnel> GetGrpcTunnels()
        {
            var now = DateTimeOffset.UtcNow;
            return GlobalState.Connections.Values
                .Where(x => x.Kind == ConnectionKind.Grpc && x.ClientIp != null && x.Target != null)
                .Select(x => new RuntimeTunnel(x.ClientIp!, x.Target!, x.DateTime))
                .ToList();
        }
    }
}
