using CoreProxy.Server.Orleans.Services;
using Grpc.Core;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using System.IO;
using System.Net.Sockets;

namespace CoreProxy.Server.Orleans.Tests
{
    /// <summary>
    /// 转发收尾异常的分级：客户端主动断开/RST 是【正常收尾】，不能按 Error 记。
    /// 回归保护 —— 曾经每次正常请求结束都刷一条 "数据流转发异常" error（实测 175 条隧道刷出 104 条），
    /// 淹没真实故障。这里钉住分类，防止再退化成一律 LogError。
    /// </summary>
    [TestClass]
    public sealed class MyGrpcServiceTeardownClassificationTests
    {
        [TestMethod]
        public void ClientResetRequestStream_IsTeardown()
        {
            // 客户端 RST 请求流时 Kestrel/gRPC 抛的就是这个（实测消息文本）
            Assert.IsTrue(MyGrpcService.IsClientTeardown(new IOException("The client reset the request stream.")));
        }

        [DataTestMethod]
        [DataRow(StatusCode.Cancelled)]
        [DataRow(StatusCode.Aborted)]
        public void RpcCancelledOrAborted_IsTeardown(StatusCode code)
        {
            Assert.IsTrue(MyGrpcService.IsClientTeardown(new RpcException(new Status(code, "teardown"))));
        }

        // 目标服务器不可达/中途断开是真实故障，必须仍记为 Error
        [TestMethod]
        public void TargetSocketError_IsNotTeardown()
        {
            Assert.IsFalse(MyGrpcService.IsClientTeardown(new SocketException((int)SocketError.ConnectionReset)));
        }

        [DataTestMethod]
        [DataRow(StatusCode.Unavailable)]
        [DataRow(StatusCode.InvalidArgument)]
        [DataRow(StatusCode.Internal)]
        public void OtherRpcStatus_IsNotTeardown(StatusCode code)
        {
            Assert.IsFalse(MyGrpcService.IsClientTeardown(new RpcException(new Status(code, "fault"))));
        }

        [TestMethod]
        public void GenericAndCancellation_AreNotClassifiedAsTeardownHere()
        {
            // OperationCanceledException 由上游专门的 catch 分支处理，这里不重复归类
            Assert.IsFalse(MyGrpcService.IsClientTeardown(new OperationCanceledException()));
            Assert.IsFalse(MyGrpcService.IsClientTeardown(new InvalidOperationException("boom")));
        }
    }
}
