using CoreProxy.Server.Orleans.Internal;
using CoreProxy.Server.Orleans.Services;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace CoreProxy.Server.Orleans.Tests
{
    /// <summary>
    /// 拆字段后 ClientIpAddress 变成派生属性，必须与拆分前的原始拼接串逐字节一致，
    /// 否则 gRPC GetStatus 输出、Exe 日志、以及 StreamHandlerTests 的断言都会静默漂移。
    /// 三种写入方（gRPC / SignalR / HTTP）各有各的拼法，逐一对齐。
    /// </summary>
    [TestClass]
    public sealed class ConnectItemTests
    {
        [TestMethod]
        public void DerivedClientIpAddress_CombinesBoth_WhenBothPresent()
        {
            // gRPC 隧道：原串是 "{peer} {host}:{port}"
            var item = new ConnectItem
            {
                Kind = ConnectionKind.Grpc,
                ClientIp = "1.2.3.4",
                Target = "10.0.0.5:8080",
                DateTime = DateTimeOffset.UtcNow
            };

            Assert.AreEqual("1.2.3.4 10.0.0.5:8080", item.ClientIpAddress);
        }

        [TestMethod]
        public void DerivedClientIpAddress_OnlyIp_WhenTargetNull()
        {
            // SignalR EchoStream：原串只有远程 IP，不能多出尾空格
            var item = new ConnectItem
            {
                Kind = ConnectionKind.Signalr,
                ClientIp = "5.6.7.8",
                Target = null,
                DateTime = DateTimeOffset.UtcNow
            };

            Assert.AreEqual("5.6.7.8", item.ClientIpAddress);
        }

        [TestMethod]
        public void DerivedClientIpAddress_OnlyTarget_WhenClientIpNull()
        {
            // HTTP /stream：原串只有 "{host}:{port}"
            var item = new ConnectItem
            {
                Kind = ConnectionKind.Http,
                ClientIp = null,
                Target = "10.0.0.9:9000",
                DateTime = DateTimeOffset.UtcNow
            };

            Assert.AreEqual("10.0.0.9:9000", item.ClientIpAddress);
        }

        [TestMethod]
        public void DerivedClientIpAddress_UsesRawPeer_WhenPeerPresent()
        {
            // gRPC 隧道：原始串是 "{context.Peer} {host}:{port}"，Peer 形如 ipv4:1.2.3.4:54321。
            // 必须逐字节保真 —— gRPC GetStatus 的 IpAddress 由此派生，不能因为页面想要干净 IP 就改掉它。
            var item = new ConnectItem
            {
                Kind = ConnectionKind.Grpc,
                Peer = "ipv4:127.0.0.1:54321",
                ClientIp = "127.0.0.1",
                Target = "10.0.0.5:8080",
                DateTime = DateTimeOffset.UtcNow
            };

            Assert.AreEqual("ipv4:127.0.0.1:54321 10.0.0.5:8080", item.ClientIpAddress);
        }
    }

    /// <summary>
    /// gRPC 写入方用 StripPeerToIp 把带 scheme 前缀 + 端口的 Peer 串剥成纯 IP，
    /// 否则管理页客户端 IP 列会显示成 ipv4:127.0.0.1:54321。
    /// </summary>
    [TestClass]
    public sealed class StripPeerToIpTests
    {
        [TestMethod]
        public void StripsIpv4SchemeAndPort()
        {
            Assert.AreEqual("127.0.0.1", MyGrpcService.StripPeerToIp("ipv4:127.0.0.1:54321"));
        }

        [TestMethod]
        public void StripsIpv6SchemeAndPort_UnbracketsAddress()
        {
            Assert.AreEqual("::1", MyGrpcService.StripPeerToIp("ipv6:[::1]:54321"));
        }

        [TestMethod]
        public void ReturnsNull_WhenUnparseable()
        {
            Assert.IsNull(MyGrpcService.StripPeerToIp("not-a-peer"));
            Assert.IsNull(MyGrpcService.StripPeerToIp(""));
            Assert.IsNull(MyGrpcService.StripPeerToIp(null));
        }
    }
}
