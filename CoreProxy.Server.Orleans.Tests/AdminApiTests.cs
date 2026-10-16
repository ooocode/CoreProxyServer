using CoreProxy.Server.Orleans.Internal;
using CoreProxy.Server.Orleans.Services;
using Microsoft.AspNetCore.Http;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using System.Text;

namespace CoreProxy.Server.Orleans.Tests
{
    /// <summary>
    /// 管理页的数据来源：只列 gRPC 隧道。SignalR EchoStream / HTTP /stream 的记录不上榜
    /// （它们缺目标或缺客户端 IP），所以过滤必须按 Kind 而不是"有无某字段"。
    /// </summary>
    [TestClass]
    public sealed class ConnectionRepositoryTests
    {
        [TestCleanup]
        public void Cleanup() => GlobalState.Connections.Clear();

        [TestMethod]
        public void GetGrpcTunnels_OnlyReturnsGrpcWithAllFields()
        {
            GlobalState.Connections.Clear();
            var startedAt = DateTimeOffset.UtcNow;
            GlobalState.Connections.TryAdd("g1", new ConnectItem { Kind = ConnectionKind.Grpc, ClientIp = "1.1.1.1", Target = "10.0.0.1:80", DateTime = startedAt });
            GlobalState.Connections.TryAdd("s1", new ConnectItem { Kind = ConnectionKind.Signalr, ClientIp = "2.2.2.2", DateTime = DateTimeOffset.UtcNow });
            GlobalState.Connections.TryAdd("h1", new ConnectItem { Kind = ConnectionKind.Http, Target = "10.0.0.2:90", DateTime = DateTimeOffset.UtcNow });

            var items = ConnectionRepository.GetGrpcTunnels();

            Assert.AreEqual(1, items.Count, "只应返回 gRPC 隧道");
            Assert.AreEqual("1.1.1.1", items[0].ClientIp);
            Assert.AreEqual("10.0.0.1:80", items[0].Target);
            Assert.AreEqual(startedAt, items[0].StartedAt, "StartedAt 应等于建连时间");
        }

        [TestMethod]
        public void GetGrpcTunnels_ReturnsEmpty_WhenNoGrpcTunnels()
        {
            GlobalState.Connections.Clear();
            GlobalState.Connections.TryAdd("s1", new ConnectItem { Kind = ConnectionKind.Signalr, ClientIp = "2.2.2.2", DateTime = DateTimeOffset.UtcNow });

            Assert.AreEqual(0, ConnectionRepository.GetGrpcTunnels().Count);
        }
    }

    /// <summary>
    /// 管理页与 /api/status 的凭据校验（HTTP Basic）。不做回环豁免：本机访问同样要密码。
    /// </summary>
    [TestClass]
    public sealed class AdminApiTests
    {
        [TestCleanup]
        public void Cleanup() => GlobalState.Connections.Clear();

        private static DefaultHttpContext CtxWith(string? header)
        {
            var ctx = new DefaultHttpContext();
            if (header != null)
            {
                ctx.Request.Headers.Authorization = header;
            }
            return ctx;
        }

        private static string Basic(string credential) =>
            "Basic " + Convert.ToBase64String(Encoding.UTF8.GetBytes(credential));

        [TestMethod]
        public void CheckBasicAuth_AcceptsCorrectPassword()
        {
            var pw = "abc123";
            Assert.IsTrue(AdminApi.CheckBasicAuth(CtxWith(Basic(":" + pw)), pw));
        }

        [TestMethod]
        public void CheckBasicAuth_AcceptsCorrectPassword_WithUserName()
        {
            Assert.IsTrue(AdminApi.CheckBasicAuth(CtxWith(Basic("admin:abc123")), "abc123"));
        }

        [TestMethod]
        public void CheckBasicAuth_RejectsWrongPassword()
        {
            Assert.IsFalse(AdminApi.CheckBasicAuth(CtxWith(Basic("user:wrong")), "right"));
        }

        [TestMethod]
        public void CheckBasicAuth_InvalidBase64_ReturnsFalseWithoutThrow()
        {
            Assert.IsFalse(AdminApi.CheckBasicAuth(CtxWith("Basic not-base64!"), "any"));
        }

        [TestMethod]
        public void CheckBasicAuth_MissingHeader_ReturnsFalse()
        {
            Assert.IsFalse(AdminApi.CheckBasicAuth(CtxWith(null), "any"));
        }

        [TestMethod]
        public void CheckBasicAuth_NonBasicScheme_ReturnsFalse()
        {
            Assert.IsFalse(AdminApi.CheckBasicAuth(CtxWith("Bearer abc123"), "abc123"));
        }

        [TestMethod]
        public async Task HandleStatus_WithoutAuth_Returns401WithWwwAuthenticate()
        {
            GlobalState.Connections.Clear();
            var ctx = CtxWith(null);
            await AdminApi.HandleStatusAsync(ctx, "pw");
            Assert.AreEqual(401, ctx.Response.StatusCode);
            Assert.AreEqual("Basic realm=\"admin\"", ctx.Response.Headers.WWWAuthenticate.ToString());
        }

        [TestMethod]
        public async Task HandleStatus_WithAuth_ReturnsOnlyGrpcTunnels()
        {
            GlobalState.Connections.Clear();
            GlobalState.Connections.TryAdd("g", new ConnectItem { Kind = ConnectionKind.Grpc, ClientIp = "1.1.1.1", Target = "10.0.0.1:80", DateTime = DateTimeOffset.UtcNow });
            GlobalState.Connections.TryAdd("s", new ConnectItem { Kind = ConnectionKind.Signalr, ClientIp = "2.2.2.2", DateTime = DateTimeOffset.UtcNow });
            var pw = "pw";
            var ctx = CtxWith(Basic(":" + pw));
            ctx.Response.Body = new MemoryStream();
            await AdminApi.HandleStatusAsync(ctx, pw);
            ctx.Response.Body.Position = 0;
            var json = await new StreamReader(ctx.Response.Body).ReadToEndAsync();
            Assert.AreEqual(200, ctx.Response.StatusCode);
            Assert.IsTrue(json.Contains("\"clientIp\":\"1.1.1.1\""), "gRPC 客户端 IP 应在，但 JSON=" + json);
            Assert.IsFalse(json.Contains("2.2.2.2"), "SignalR 记录不应上榜，但 JSON=" + json);
            Assert.IsTrue(json.Contains("\"total\":1"), "total 只应数 gRPC，但 JSON=" + json);
        }
    }
}
