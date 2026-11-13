using System.Collections.Concurrent;
using System.Diagnostics.CodeAnalysis;

namespace CoreProxy.Server.Orleans.Internal
{
    /// <summary>
    /// 当前活跃隧道的进程内登记表（DI 单例）。
    /// 原先的全局静态字典改为实例：测试可以各自持有一份，生产由容器注入同一实例。
    /// </summary>
    public sealed class TunnelRegistry
    {
        private readonly ConcurrentDictionary<string, ConnectItem> connections = new();

        public int Count => connections.Count;

        public bool IsEmpty => connections.IsEmpty;

        public bool TryAdd(string connectionId, ConnectItem item) => connections.TryAdd(connectionId, item);

        public bool TryRemove(string connectionId, [MaybeNullWhen(false)] out ConnectItem item) => connections.TryRemove(connectionId, out item);

        /// <summary>
        /// 返回当前连接记录的快照。管理页等异步消费者拿到的是一份独立数组，
        /// 不会在枚举过程中因隧道上下线抛异常。
        /// </summary>
        public IReadOnlyList<ConnectItem> Snapshot() => connections.Values.ToArray();

        /// <summary>
        /// 只取 gRPC 隧道的当前连接记录，供管理页 /api/status 展示。
        /// SignalR EchoStream / HTTP /stream 的记录（缺目标或缺客户端 IP）一律不上榜。
        /// </summary>
        public List<RuntimeTunnel> GetGrpcTunnels()
        {
            return connections.Values
                .Where(x => x.Kind == ConnectionKind.Grpc && x.ClientIp != null && x.Target != null)
                .Select(x => new RuntimeTunnel(x.ClientIp!, x.Target!, x.DateTime))
                .ToList();
        }

        internal void ClearForTests() => connections.Clear();
    }
}
