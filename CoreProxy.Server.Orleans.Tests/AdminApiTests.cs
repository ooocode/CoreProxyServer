using CoreProxy.Server.Orleans.Internal;
using CoreProxy.Server.Orleans.Services;
using Hello;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.SignalR;
using Microsoft.Extensions.DependencyInjection;
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
        public void Cleanup()
        {
            GlobalState.Connections.Clear();
            GloableSessionsManager.SignalrOnlineClients.Clear();
        }

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

        // 受保护路径的判定必须覆盖 /admin.html。/admin 与 /admin.html 是两个不同的路径段，
        // StartsWithSegments("/admin") 匹配不到后者——这里正是曾出现过的静态文件绕过鉴权 bug。
        [DataTestMethod]
        [DataRow("/admin", true)]
        [DataRow("/admin/", true)]
        [DataRow("/admin/index.html", true)]
        [DataRow("/admin.html", true)]
        [DataRow("/ADMIN.HTML", true)]
        [DataRow("/api/status", true)]
        [DataRow("/api/devices", true)]
        [DataRow("/api/devices/", true)]
        [DataRow("/api/devicesx", false)] // 同前缀但不同路径段，不应被误匹配
        [DataRow("/api/invoke", true)]
        [DataRow("/api/invoke/", true)]
        [DataRow("/api/invokex", false)] // 同前缀但不同路径段，不应被误匹配
        [DataRow("/api/statusx", false)]
        [DataRow("/", false)]
        [DataRow("/administrator", false)]
        [DataRow("/chathub", false)]
        [DataRow("/push/abc", false)]
        public void RequiresAuth_MatchesProtectedPathsOnly(string path, bool expected)
        {
            Assert.AreEqual(expected, AdminApi.RequiresAuth(new PathString(path)), "path=" + path);
        }

        /// <summary>
        /// 跑一次返回 IResult 的 GET/管理处理器，并把结果落到 HttpContext 上，返回该 ctx 供断言。
        /// IResult 执行时要从 RequestServices 取服务（ILoggerFactory 等），真实应用中由框架注入，这里补最小容器。
        /// </summary>
        private static async Task<DefaultHttpContext> RunGet(string? header, Func<HttpContext, IResult> handler)
        {
            var ctx = CtxWith(header);
            ctx.Response.Body = new MemoryStream();
            ctx.RequestServices = new ServiceCollection().AddLogging().BuildServiceProvider();
            await handler(ctx).ExecuteAsync(ctx);
            return ctx;
        }

        private static async Task<string> ReadBodyAsync(DefaultHttpContext ctx)
        {
            ctx.Response.Body.Position = 0;
            return await new StreamReader(ctx.Response.Body).ReadToEndAsync();
        }

        [TestMethod]
        public async Task HandleStatus_WithoutAuth_Returns401WithWwwAuthenticate()
        {
            GlobalState.Connections.Clear();
            var ctx = await RunGet(null, c => AdminApi.HandleStatus(c, "pw"));
            Assert.AreEqual(401, ctx.Response.StatusCode);
            Assert.AreEqual("Basic realm=\"admin\"", ctx.Response.Headers.WWWAuthenticate.ToString());
        }

        [TestMethod]
        public async Task HandleStatus_WithAuth_ReturnsOnlyGrpcTunnels()
        {
            GlobalState.Connections.Clear();
            GlobalState.Connections.TryAdd("g", new ConnectItem { Kind = ConnectionKind.Grpc, ClientIp = "1.1.1.1", Target = "10.0.0.1:80", DateTime = DateTimeOffset.UtcNow });
            GlobalState.Connections.TryAdd("s", new ConnectItem { Kind = ConnectionKind.Signalr, ClientIp = "2.2.2.2", DateTime = DateTimeOffset.UtcNow });

            var ctx = await RunGet(Basic(":pw"), c => AdminApi.HandleStatus(c, "pw"));
            var json = await ReadBodyAsync(ctx);
            Assert.AreEqual(200, ctx.Response.StatusCode);
            Assert.IsTrue(json.Contains("\"clientIp\":\"1.1.1.1\""), "gRPC 客户端 IP 应在，但 JSON=" + json);
            Assert.IsFalse(json.Contains("2.2.2.2"), "SignalR 记录不应上榜，但 JSON=" + json);
            Assert.IsTrue(json.Contains("\"total\":1"), "total 只应数 gRPC，但 JSON=" + json);
        }

        [TestMethod]
        public async Task HandleDevices_WithoutAuth_Returns401()
        {
            var ctx = await RunGet(null, c => AdminApi.HandleDevices(c, "pw"));
            Assert.AreEqual(401, ctx.Response.StatusCode);
            Assert.AreEqual("Basic realm=\"admin\"", ctx.Response.Headers.WWWAuthenticate.ToString());
        }

        [TestMethod]
        public async Task HandleDevices_WithAuth_ListsOnlineDevicesAndTheirReportedMethods()
        {
            void Online(string id, string conn, params string[] methods)
            {
                var c = new SignalrOnlineClient { DeviceId = id, MachineName = "pc-" + id, UserName = "user-" + id, ConnectionId = conn };
                c.Methods.AddRange(methods);
                GloableSessionsManager.SignalrOnlineClients.TryAdd(conn, c);
            }
            Online("b", "conn-b", "Reboot");                 // 单个方法
            Online("a", "conn-a", "SessionStart", "Reboot"); // 多个方法
            Online("c", "conn-c");                           // 未上报方法

            var ctx = await RunGet(Basic(":pw"), c => AdminApi.HandleDevices(c, "pw"));
            var json = await ReadBodyAsync(ctx);
            Assert.AreEqual(200, ctx.Response.StatusCode);
            Assert.IsTrue(json.Contains("\"total\":3"), "应按设备数计数，但 JSON=" + json);
            Assert.IsTrue(json.Contains("\"deviceId\":\"a\""), "应列出设备 a，但 JSON=" + json);
            Assert.IsTrue(json.Contains("\"machineName\":\"pc-a\""), "应含机器名，但 JSON=" + json);
            Assert.IsTrue(json.Contains("\"methods\":[\"SessionStart\",\"Reboot\"]"), "a 应上报两个方法，但 JSON=" + json);
            Assert.IsTrue(json.Contains("\"methods\":[\"Reboot\"]"), "b 应上报 Reboot，但 JSON=" + json);
            Assert.IsTrue(json.Contains("\"methods\":[]"), "c 未上报应为空数组，但 JSON=" + json);
            Assert.IsTrue(json.IndexOf("\"deviceId\":\"a\"") < json.IndexOf("\"deviceId\":\"b\""), "应按 DeviceId 排序，但 JSON=" + json);
        }

        // 同一 deviceId 允许多条连接同时在线：每条连接独立一行，total 数连接数，
        // 排序用 DeviceId + ConnectionId，保证同 ID 的多条连接可区分且输出稳定。
        [TestMethod]
        public async Task HandleDevices_DuplicateDeviceId_ListsEveryConnectionSortedByConnectionId()
        {
            void Online(string id, string conn, params string[] methods)
            {
                var c = new SignalrOnlineClient { DeviceId = id, MachineName = "pc-" + id, UserName = "user-" + id, ConnectionId = conn };
                c.Methods.AddRange(methods);
                GloableSessionsManager.SignalrOnlineClients.TryAdd(conn, c);
            }
            Online("d1", "conn-b", "Ping");
            Online("d1", "conn-a", "Ping", "Echo");
            Online("d2", "conn-c");

            var ctx = await RunGet(Basic(":pw"), c => AdminApi.HandleDevices(c, "pw"));
            var json = await ReadBodyAsync(ctx);

            Assert.AreEqual(200, ctx.Response.StatusCode);
            Assert.IsTrue(json.Contains("\"total\":3"), "total 应数连接而非设备，JSON=" + json);
            Assert.AreEqual(2, json.Split("\"deviceId\":\"d1\"").Length - 1, "d1 的两条连接都应列出，JSON=" + json);
            var a = json.IndexOf("\"connectionId\":\"conn-a\"");
            var b = json.IndexOf("\"connectionId\":\"conn-b\"");
            var c = json.IndexOf("\"connectionId\":\"conn-c\"");
            Assert.IsTrue(a >= 0 && b >= 0 && c >= 0, "三条连接都应列出，JSON=" + json);
            Assert.IsTrue(a < b && b < c, "应按 DeviceId + ConnectionId 排序，JSON=" + json);
        }
    }

    /// <summary>
    /// /api/invoke：带 deviceId 时精确调用该在线设备上报的方法；不带 deviceId 时在所有上报了该方法的
    /// 在线客户端中随机选一个。两条路径都校验设备【主动上报】的 Methods，再经 SignalR 调用并透传返回值。
    /// 请求体现在由 minimal API 强类型绑定（Program.cs 的 MapPost），故这里直接以 <see cref="InvokeRequest"/> 调用
    /// HandleInvokeAsync——"JSON 非法 → 400"由绑定层负责，不在此覆盖。
    /// 端上调用（InvokeAsync 等返回值）依赖 SignalR 协议握手、无法用假 IHubContext 复现，故用 invokeOnClient 注入替换，
    /// 聚焦 HTTP 分层语义、方法白名单、随机候选过滤与透传正确性。
    /// </summary>
    [TestClass]
    public sealed class InvokeApiTests
    {
        private static readonly NullHubContext HubContext = new();

        [TestCleanup]
        public void Cleanup() => GloableSessionsManager.SignalrOnlineClients.Clear();

        private static string Basic(string credential) =>
            "Basic " + Convert.ToBase64String(Encoding.UTF8.GetBytes(credential));

        /// <summary>注册一条已上报若干方法名的在线连接（同一 deviceId 可注册多条不同 connectionId 的连接）。</summary>
        private static void Online(string deviceId, string connectionId, params string[] methods)
        {
            var client = new SignalrOnlineClient { DeviceId = deviceId, ConnectionId = connectionId };
            client.Methods.AddRange(methods);
            GloableSessionsManager.SignalrOnlineClients.TryAdd(connectionId, client);
        }

        /// <summary>绝不该被调用的端上调用实现，用于断言"未通过校验就不下发"。</summary>
        private static Task<string> MustNotInvoke(string connectionId, string method, string payload, CancellationToken cancellationToken) =>
            throw new AssertFailedException($"不该调用端上方法 {method}");

        /// <summary>跑一次 HandleInvokeAsync，并把返回的 IResult 落到 HttpContext 上，返回该 ctx 供断言。</summary>
        private static async Task<DefaultHttpContext> Run(string? header, InvokeRequest request,
            AdminApi.InvokeOnClient? invokeOnClient = null)
        {
            var ctx = new DefaultHttpContext();
            if (header != null)
            {
                ctx.Request.Headers.Authorization = header;
            }
            ctx.Response.Body = new MemoryStream();
            // IResult（StatusCodeHttpResult / JsonHttpResult 等）执行时需要 RequestServices 里的服务
            // （ILoggerFactory、IJsonOptions…），真实应用中由框架注入，这里补 + 最小日志容器。
            ctx.RequestServices = new ServiceCollection().AddLogging().BuildServiceProvider();

            var result = await AdminApi.HandleInvokeAsync(request, "pw", ctx, HubContext, invokeOnClient);
            await result.ExecuteAsync(ctx);
            return ctx;
        }

        private static string ReadBody(DefaultHttpContext ctx)
        {
            ctx.Response.Body.Position = 0;
            return new StreamReader(ctx.Response.Body).ReadToEnd();
        }

        [TestMethod]
        public async Task Invoke_WithoutAuth_Returns401()
        {
            var ctx = await Run(null, new InvokeRequest("d1", "SessionStart", "p"));
            Assert.AreEqual(401, ctx.Response.StatusCode);
            Assert.AreEqual("Basic realm=\"admin\"", ctx.Response.Headers.WWWAuthenticate.ToString());
        }

        [TestMethod]
        public async Task Invoke_NoDeviceId_NoOnlineDevices_Returns404WithoutInvoking()
        {
            var ctx = await Run(Basic(":pw"), new InvokeRequest(null, "SessionStart", "p"), MustNotInvoke);
            Assert.AreEqual(404, ctx.Response.StatusCode);
        }

        // 空白 deviceId 等价于不传：走随机模式。有设备在线但没人上报该方法 → 没有可承接的目标 → 404。
        [TestMethod]
        public async Task Invoke_BlankDeviceId_NoClientReportedMethod_Returns404WithoutInvoking()
        {
            Online("d1", "conn-1", "Reboot");
            Online("d2", "conn-2"); // 未上报任何方法

            var ctx = await Run(Basic(":pw"), new InvokeRequest("  ", "SessionStart", "p"), MustNotInvoke);

            Assert.AreEqual(404, ctx.Response.StatusCode);
        }

        // 随机模式的核心：候选只能来自上报了该方法的客户端；不在候选里的设备绝不能被调用。
        // 200 轮下两台候选都至少被选中一次的概率为 1 - 2^-199，足以排除"永远只选第一台"这类回归。
        [TestMethod]
        public async Task Invoke_NoDeviceId_RandomlySelectsAmongClientsReportingMethod()
        {
            Online("reboot-only", "conn-r", "Reboot");
            Online("start-a", "conn-a", "SessionStart");
            Online("start-b", "conn-b", "SessionStart");

            var hits = new Dictionary<string, int>(StringComparer.Ordinal);
            var devicesByConn = new Dictionary<string, string>(StringComparer.Ordinal)
            {
                ["conn-a"] = "start-a",
                ["conn-b"] = "start-b",
            };

            string? lastConn = null;
            Task<string> Invoke(string connId, string method, string payload, CancellationToken _)
            {
                lastConn = connId;
                hits[connId] = hits.GetValueOrDefault(connId) + 1;
                Assert.AreEqual("SessionStart", method, "应下发设备上报的精确拼写");
                Assert.AreEqual("hello", payload, "payload 应原样透传");
                return Task.FromResult("ok:" + connId);
            }

            const int rounds = 200;
            for (var i = 0; i < rounds; i++)
            {
                var ctx = await Run(Basic(":pw"), new InvokeRequest(null, "SessionStart", "hello"), Invoke);
                Assert.AreEqual(200, ctx.Response.StatusCode, "第 " + i + " 轮");

                var json = ReadBody(ctx);
                var conn = lastConn!;
                Assert.IsTrue(devicesByConn.ContainsKey(conn), "只应选中上报了该方法的设备，实际 conn=" + conn);
                Assert.IsTrue(json.Contains($"\"deviceId\":\"{devicesByConn[conn]}\""),
                    "响应应回显实际承接调用的设备，JSON=" + json);
                Assert.IsTrue(json.Contains($"\"connectionId\":\"{conn}\""),
                    "响应应回显实际承接调用的连接，JSON=" + json);
                Assert.IsTrue(json.Contains($"\"result\":\"ok:{conn}\""), "JSON=" + json);
            }

            Assert.IsFalse(hits.ContainsKey("conn-r"), "未上报该方法的设备不可能是候选");
            Assert.IsTrue(hits.GetValueOrDefault("conn-a") > 0, "随机 200 轮中 conn-a 应至少命中一次");
            Assert.IsTrue(hits.GetValueOrDefault("conn-b") > 0, "随机 200 轮中 conn-b 应至少命中一次");
        }

        // 随机模式下仍要用【被选中设备上报的精确拼写】下发，而不是请求原文。
        [TestMethod]
        public async Task Invoke_NoDeviceId_UsesReportedSpellingOfSelectedDevice()
        {
            Online("d1", "conn-1", "SeSsIoNStart");

            string? seenMethod = null;
            Task<string> Invoke(string connId, string method, string payload, CancellationToken _)
            {
                seenMethod = method;
                return Task.FromResult("ok");
            }

            var ctx = await Run(Basic(":pw"), new InvokeRequest(null, "sessionstart", "p"), Invoke);

            Assert.AreEqual(200, ctx.Response.StatusCode);
            Assert.AreEqual("SeSsIoNStart", seenMethod, "应下发设备上报的精确拼写");
            Assert.IsTrue(ReadBody(ctx).Contains("\"method\":\"SeSsIoNStart\""), "响应 method 应为实际下发的名字");
        }

        [TestMethod]
        public async Task Invoke_NoDeviceId_NullPayload_DefaultsToEmptyString()
        {
            Online("d1", "conn-1", "SessionStart");

            string? seenPayload = null;
            Task<string> Invoke(string connectionId, string method, string payload, CancellationToken cancellationToken)
            {
                seenPayload = payload;
                return Task.FromResult("ok");
            }

            var ctx = await Run(Basic(":pw"), new InvokeRequest(null, "SessionStart", null), Invoke);

            Assert.AreEqual(200, ctx.Response.StatusCode);
            Assert.AreEqual(string.Empty, seenPayload);
        }

        [TestMethod]
        public async Task Invoke_NoDeviceId_ClientFault_Returns502()
        {
            Online("d1", "conn-1", "SessionStart");
            static Task<string> Invoke(string connectionId, string method, string payload, CancellationToken cancellationToken) =>
                throw new InvalidOperationException("设备已断开");

            var ctx = await Run(Basic(":pw"), new InvokeRequest(null, "SessionStart", "p"), Invoke);
            Assert.AreEqual(502, ctx.Response.StatusCode);
        }

        // 同一 DeviceId 允许多条连接：精确指定 d1 时，只在 d1 下【支持该方法】的连接中随机选，
        // 同 ID 但不支持方法的连接、以及其它 DeviceId 的连接都不能成为候选。
        [TestMethod]
        public async Task Invoke_PinnedDeviceId_RandomlySelectsAmongItsConnectionsSupportingMethod()
        {
            Online("d1", "conn-a", "Ping");
            Online("d1", "conn-b", "Ping");
            Online("d1", "conn-c", "Other");
            Online("d2", "conn-x", "Ping");

            var hits = new Dictionary<string, int>(StringComparer.Ordinal);
            string? lastConn = null;
            Task<string> Invoke(string connId, string method, string payload, CancellationToken _)
            {
                lastConn = connId;
                hits[connId] = hits.GetValueOrDefault(connId) + 1;
                Assert.AreEqual("Ping", method);
                return Task.FromResult("ok:" + connId);
            }

            const int rounds = 200;
            for (var i = 0; i < rounds; i++)
            {
                var ctx = await Run(Basic(":pw"), new InvokeRequest("d1", "Ping", "p"), Invoke);
                Assert.AreEqual(200, ctx.Response.StatusCode, "第 " + i + " 轮");

                var json = ReadBody(ctx);
                var conn = lastConn!;
                Assert.IsTrue(conn is "conn-a" or "conn-b", "只应选中 d1 下支持 Ping 的连接，实际 " + conn);
                Assert.IsTrue(json.Contains("\"deviceId\":\"d1\""), "JSON=" + json);
                Assert.IsTrue(json.Contains($"\"connectionId\":\"{conn}\""), "JSON=" + json);
            }

            Assert.IsFalse(hits.ContainsKey("conn-c"), "同 ID 但未上报该方法的连接不可能是候选");
            Assert.IsFalse(hits.ContainsKey("conn-x"), "其它 DeviceId 的连接不可能是候选");
            Assert.IsTrue(hits.GetValueOrDefault("conn-a") > 0, "随机 200 轮中 conn-a 应至少命中一次");
            Assert.IsTrue(hits.GetValueOrDefault("conn-b") > 0, "随机 200 轮中 conn-b 应至少命中一次");
        }

        [TestMethod]
        public async Task Invoke_PinnedDeviceId_OnlyOneConnectionSupportsMethod_AlwaysSelectsIt()
        {
            Online("d1", "conn-a", "Ping");
            Online("d1", "conn-b", "Other");

            string? seenConn = null;
            Task<string> Invoke(string connId, string method, string payload, CancellationToken _)
            {
                seenConn = connId;
                return Task.FromResult("ok");
            }

            var ctx = await Run(Basic(":pw"), new InvokeRequest("d1", "Ping", "p"), Invoke);

            Assert.AreEqual(200, ctx.Response.StatusCode);
            Assert.AreEqual("conn-a", seenConn, "只有 conn-a 支持 Ping");
        }

        [TestMethod]
        public async Task Invoke_PinnedDeviceId_NoConnectionSupportsMethod_Returns409WithoutInvoking()
        {
            Online("d1", "conn-a", "Other");
            Online("d1", "conn-b", "Shutdown");

            var ctx = await Run(Basic(":pw"), new InvokeRequest("d1", "Ping", "p"), MustNotInvoke);

            Assert.AreEqual(409, ctx.Response.StatusCode);
        }

        // 全局随机模式下，同一 DeviceId 的多条连接是彼此独立的候选；不能被字典键合并成一条。
        [TestMethod]
        public async Task Invoke_NoDeviceId_DuplicateDeviceIds_AllSupportingConnectionsAreCandidates()
        {
            Online("d1", "conn-a", "Ping");
            Online("d1", "conn-b", "Ping");
            Online("d2", "conn-x", "Other");

            var hits = new Dictionary<string, int>(StringComparer.Ordinal);
            string? lastConn = null;
            Task<string> Invoke(string connId, string method, string payload, CancellationToken _)
            {
                lastConn = connId;
                hits[connId] = hits.GetValueOrDefault(connId) + 1;
                return Task.FromResult("ok:" + connId);
            }

            const int rounds = 200;
            for (var i = 0; i < rounds; i++)
            {
                var ctx = await Run(Basic(":pw"), new InvokeRequest(null, "Ping", "p"), Invoke);
                Assert.AreEqual(200, ctx.Response.StatusCode, "第 " + i + " 轮");
                Assert.IsTrue(lastConn is "conn-a" or "conn-b", "实际 " + lastConn);
                Assert.IsTrue(ReadBody(ctx).Contains($"\"connectionId\":\"{lastConn}\""), "响应应回显实际连接");
            }

            Assert.IsFalse(hits.ContainsKey("conn-x"));
            Assert.IsTrue(hits.GetValueOrDefault("conn-a") > 0);
            Assert.IsTrue(hits.GetValueOrDefault("conn-b") > 0);
        }

        [TestMethod]
        public async Task Invoke_BlankMethod_Returns400()
        {
            var ctx = await Run(Basic(":pw"), new InvokeRequest("d1", "", "p"));
            Assert.AreEqual(400, ctx.Response.StatusCode);
        }

        [TestMethod]
        public async Task Invoke_DeviceOffline_Returns404()
        {
            var ctx = await Run(Basic(":pw"), new InvokeRequest("missing", "SessionStart", "p"));
            Assert.AreEqual(404, ctx.Response.StatusCode);
        }

        // 设备在线、但没上报这个方法 → 409，且绝不下发给设备（方法白名单是这段逻辑的核心）。
        [TestMethod]
        public async Task Invoke_MethodNotReportedByDevice_Returns409WithoutInvoking()
        {
            Online("d1", "conn-1", "Reboot", "Shutdown");

            var ctx = await Run(Basic(":pw"), new InvokeRequest("d1", "SessionStart", "p"), MustNotInvoke);

            Assert.AreEqual(409, ctx.Response.StatusCode);
        }

        // 设备一个方法都没上报 → 任何调用都 409。
        [TestMethod]
        public async Task Invoke_DeviceReportedNoMethods_Returns409()
        {
            Online("d1", "conn-1");

            var ctx = await Run(Basic(":pw"), new InvokeRequest("d1", "SessionStart", "p"), MustNotInvoke);

            Assert.AreEqual(409, ctx.Response.StatusCode);
        }

        [TestMethod]
        public async Task Invoke_ReportedMethod_InvokesThatMethodAndReturns200WithResult()
        {
            Online("d1", "conn-1", "Reboot", "SessionStart");

            string? seenConn = null;
            string? seenMethod = null;
            string? seenPayload = null;
            Task<string> Invoke(string connId, string method, string payload, CancellationToken _)
            {
                seenConn = connId;
                seenMethod = method;
                seenPayload = payload;
                return Task.FromResult("client-ok");
            }

            var ctx = await Run(Basic(":pw"), new InvokeRequest("d1", "SessionStart", "hello"), Invoke);

            Assert.AreEqual(200, ctx.Response.StatusCode);
            Assert.AreEqual("conn-1", seenConn, "应按在线记录的 ConnectionId 调用");
            Assert.AreEqual("SessionStart", seenMethod, "应调用请求指定的、且设备已上报的方法");
            Assert.AreEqual("hello", seenPayload, "payload 应原样透传");
            var json = ReadBody(ctx);
            Assert.IsTrue(json.Contains("\"result\":\"client-ok\""), "应透传客户端返回值，但 JSON=" + json);
            Assert.IsTrue(json.Contains("\"deviceId\":\"d1\""), "JSON=" + json);
            Assert.IsTrue(json.Contains("\"connectionId\":\"conn-1\""), "JSON=" + json);
            Assert.IsTrue(json.Contains("\"method\":\"SessionStart\""), "响应应回显方法名，但 JSON=" + json);
        }

        // 方法名大小写不敏感匹配，但下发给设备的必须是【设备上报的精确拼写】——SignalR 客户端 On
        // 处理器按精确名注册，用请求原文下发会匹配不到处理器（真实冒烟里曾因此吃到 502）。
        [TestMethod]
        public async Task Invoke_MethodMatchIsCaseInsensitive_DispatchesDeviceReportedSpelling()
        {
            Online("d1", "conn-1", "SessionStart");

            string? seenMethod = null;
            Task<string> Invoke(string connId, string method, string payload, CancellationToken _)
            {
                seenMethod = method;
                return Task.FromResult("ok");
            }

            var ctx = await Run(Basic(":pw"), new InvokeRequest("d1", "sessionstart", "p"), Invoke);

            Assert.AreEqual(200, ctx.Response.StatusCode);
            Assert.AreEqual("SessionStart", seenMethod, "应下发设备上报的精确拼写");
            Assert.IsTrue(ReadBody(ctx).Contains("\"method\":\"SessionStart\""), "响应 method 应为实际下发的名字");
        }

        [TestMethod]
        public async Task Invoke_NullPayload_DefaultsToEmptyString()
        {
            Online("d1", "conn-1", "SessionStart");

            string? seenPayload = null;
            Task<string> Invoke(string connectionId, string method, string payload, CancellationToken cancellationToken)
            {
                seenPayload = payload;
                return Task.FromResult("ok");
            }

            var ctx = await Run(Basic(":pw"), new InvokeRequest("d1", "SessionStart", null), Invoke);

            Assert.AreEqual(200, ctx.Response.StatusCode);
            Assert.AreEqual(string.Empty, seenPayload);
        }

        [TestMethod]
        public async Task Invoke_ClientFault_Returns502()
        {
            Online("d1", "conn-1", "SessionStart");
            static Task<string> Invoke(string connectionId, string method, string payload, CancellationToken cancellationToken) =>
                throw new InvalidOperationException("设备已断开");

            var ctx = await Run(Basic(":pw"), new InvokeRequest("d1", "SessionStart", "p"), Invoke);
            Assert.AreEqual(502, ctx.Response.StatusCode);
        }
    }

    /// <summary>
    /// 在测试接缝打开时不被触碰的最小 IHubContext：Clients/Groups 均返回 null（从不使用）。
    /// </summary>
    internal sealed class NullHubContext : IHubContext<ChatHub>
    {
        public IHubClients Clients => null!;
        public IGroupManager Groups => null!;
    }
}
