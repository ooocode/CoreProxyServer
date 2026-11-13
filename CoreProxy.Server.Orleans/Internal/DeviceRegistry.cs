using Hello;
using System.Collections.Concurrent;
using System.Diagnostics.CodeAnalysis;

namespace CoreProxy.Server.Orleans.Internal
{
    /// <summary>
    /// SignalR 在线客户端登记表（DI 单例）。
    /// 键是 ConnectionId 而不是 DeviceId：同一个 DeviceId 允许多条连接同时在线
    /// （重连、同机多实例等），这样断开时只移除自己那条，不会误删同 ID 的其它连接。
    /// </summary>
    public sealed class DeviceRegistry
    {
        private readonly ConcurrentDictionary<string, SignalrOnlineClient> clients = new();

        public int Count => clients.Count;

        public bool TryAdd(string connectionId, SignalrOnlineClient client) => clients.TryAdd(connectionId, client);

        public bool TryRemove(string connectionId, [MaybeNullWhen(false)] out SignalrOnlineClient client) => clients.TryRemove(connectionId, out client);

        /// <summary>返回当前在线客户端的快照，供管理 API 异步枚举时不与连接上下线竞争。</summary>
        public IReadOnlyList<SignalrOnlineClient> Snapshot() => clients.Values.ToArray();

        internal void ClearForTests() => clients.Clear();
    }
}
