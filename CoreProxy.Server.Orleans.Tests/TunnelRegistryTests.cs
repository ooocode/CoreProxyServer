using CoreProxy.Server.Orleans.Internal;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace CoreProxy.Server.Orleans.Tests
{
    /// <summary>
    /// 管理页的数据来源：只列 gRPC 隧道。SignalR EchoStream / HTTP /stream 的记录不上榜
    /// （它们缺目标或缺客户端 IP），所以过滤必须按 Kind 而不是"有无某字段"。
    /// </summary>
    [TestClass]
    public sealed class TunnelRegistryTests
    {
        [TestMethod]
        public void GetGrpcTunnels_OnlyReturnsGrpcWithAllFields()
        {
            var registry = new TunnelRegistry();
            var startedAt = DateTimeOffset.UtcNow;
            registry.TryAdd("g1", new ConnectItem { Kind = ConnectionKind.Grpc, ClientIp = "1.1.1.1", Target = "10.0.0.1:80", DateTime = startedAt });
            registry.TryAdd("s1", new ConnectItem { Kind = ConnectionKind.Signalr, ClientIp = "2.2.2.2", DateTime = DateTimeOffset.UtcNow });
            registry.TryAdd("h1", new ConnectItem { Kind = ConnectionKind.Http, Target = "10.0.0.2:90", DateTime = DateTimeOffset.UtcNow });

            var items = registry.GetGrpcTunnels();

            Assert.AreEqual(1, items.Count, "只应返回 gRPC 隧道");
            Assert.AreEqual("1.1.1.1", items[0].ClientIp);
            Assert.AreEqual("10.0.0.1:80", items[0].Target);
            Assert.AreEqual(startedAt, items[0].StartedAt, "StartedAt 应等于建连时间");
        }

        [TestMethod]
        public void GetGrpcTunnels_ReturnsEmpty_WhenNoGrpcTunnels()
        {
            var registry = new TunnelRegistry();
            registry.TryAdd("s1", new ConnectItem { Kind = ConnectionKind.Signalr, ClientIp = "2.2.2.2", DateTime = DateTimeOffset.UtcNow });

            Assert.AreEqual(0, registry.GetGrpcTunnels().Count);
        }

        [TestMethod]
        public void Snapshot_IsStableAfterMutation()
        {
            var registry = new TunnelRegistry();
            registry.TryAdd("a", new ConnectItem { Kind = ConnectionKind.Grpc, ClientIp = "1.1.1.1", Target = "t:80", DateTime = DateTimeOffset.UtcNow });

            var snapshot = registry.Snapshot();
            registry.TryAdd("b", new ConnectItem { Kind = ConnectionKind.Grpc, ClientIp = "2.2.2.2", Target = "t:81", DateTime = DateTimeOffset.UtcNow });

            Assert.AreEqual(1, snapshot.Count, "快照不应随后续写入变化");
            Assert.AreEqual(2, registry.Count);
        }
    }
}
