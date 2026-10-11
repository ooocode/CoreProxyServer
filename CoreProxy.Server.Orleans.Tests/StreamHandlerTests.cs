using CoreProxy.Server.Orleans.Internal;
using CoreProxy.Server.Orleans.Tests.Infrastructure;
using Google.Protobuf;
using Grpc.Core;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using System.Diagnostics;
using System.Net;
using System.Net.Sockets;

namespace CoreProxy.Server.Orleans.Tests
{
    [TestClass]
    public sealed class StreamHandlerTests
    {
        private static readonly TimeSpan Timeout = TimeSpan.FromSeconds(15);

        /// <summary>
        /// 半关闭判定必须短于 StreamHandler 里的 5 秒排空窗口，
        /// 否则"对端读到 EOF"可能来自超时强拆，测不出 FIN 是否真的发出去了
        /// </summary>
        private static readonly TimeSpan FinTimeout = TimeSpan.FromSeconds(3);

        /// <summary>非回环地址（TEST-NET-3），用来触发口令校验分支</summary>
        private static readonly IPAddress RemoteClient = IPAddress.Parse("203.0.113.10");

        [TestCleanup]
        public void Cleanup() => GlobalState.Connections.Clear();

        // ---------------- 握手 ----------------

        [TestMethod]
        public async Task FirstResponseIsEmptyHandshake()
        {
            using var server = new TestTargetServer();
            await using var tunnel = Tunnel.Start(server.Uri);

            using var target = await server.AcceptAsync(Timeout);
            await tunnel.ReadHandshakeAsync(Timeout);
        }

        // ---------------- 数据面 ----------------

        [TestMethod]
        public async Task ClientToTargetBytesAreForwardedExactly()
        {
            using var server = new TestTargetServer();
            await using var tunnel = Tunnel.Start(server.Uri);
            var payload = RandomBytes(2 * 1024 * 1024, seed: 1);

            var targetTask = Task.Run(async () =>
            {
                using var target = await server.AcceptAsync(Timeout);
                return await TestTargetServer.ReadExactlyAsync(target, payload.Length, Timeout);
            });

            foreach (var chunk in Chunked(payload, seed: 2))
            {
                tunnel.Send(chunk);
            }

            tunnel.CompleteClientStream();

            AssertBytesEqual(payload, await targetTask);

            //两个方向都结束后，方法应当立即返回（不等满 5 秒排空窗口）
            await tunnel.ReadHandshakeAsync(Timeout);
            await tunnel.Task.WaitAsync(Timeout);
        }

        [TestMethod]
        public async Task TargetToClientBytesAreForwardedExactly()
        {
            using var server = new TestTargetServer();
            await using var tunnel = Tunnel.Start(server.Uri);
            var payload = RandomBytes(2 * 1024 * 1024, seed: 3);

            var targetTask = Task.Run(async () =>
            {
                using var target = await server.AcceptAsync(Timeout);
                await TestTargetServer.WriteExactlyAsync(target, payload, Timeout);
            });

            await tunnel.ReadHandshakeAsync(Timeout);
            AssertBytesEqual(payload, await tunnel.ReadPayloadAsync(payload.Length, Timeout));

            await targetTask;
            tunnel.CompleteClientStream();
            await tunnel.Task.WaitAsync(Timeout);
            tunnel.AssertNoMoreData();
        }

        [TestMethod]
        public async Task BidirectionalTransferIsLossless()
        {
            using var server = new TestTargetServer();
            await using var tunnel = Tunnel.Start(server.Uri);

            var request = RandomBytes(2 * 1024 * 1024, seed: 11);
            var response = RandomBytes(2 * 1024 * 1024, seed: 12);

            var targetTask = Task.Run(async () =>
            {
                using var target = await server.AcceptAsync(Timeout);
                var readTask = TestTargetServer.ReadExactlyAsync(target, request.Length, Timeout);
                await TestTargetServer.WriteExactlyAsync(target, response, Timeout);
                return await readTask;
            });

            await tunnel.ReadHandshakeAsync(Timeout);

            var receiveTask = tunnel.ReadPayloadAsync(response.Length, Timeout);
            foreach (var chunk in Chunked(request, seed: 13))
            {
                tunnel.Send(chunk);
            }

            tunnel.CompleteClientStream();

            AssertBytesEqual(request, await targetTask);
            AssertBytesEqual(response, await receiveTask);
        }

        [TestMethod]
        public async Task EmptyMessagesDoNotCorruptStream()
        {
            using var server = new TestTargetServer();
            await using var tunnel = Tunnel.Start(server.Uri);
            var payload = RandomBytes(64 * 1024, seed: 21);

            var targetTask = Task.Run(async () =>
            {
                using var target = await server.AcceptAsync(Timeout);
                return await TestTargetServer.ReadExactlyAsync(target, payload.Length, Timeout);
            });

            tunnel.Send([]);
            tunnel.Send(payload);
            tunnel.Send([]);
            tunnel.CompleteClientStream();

            AssertBytesEqual(payload, await targetTask);
        }

        // ---------------- 半关闭 ----------------

        /// <summary>
        /// 客户端发完后，目标服务器必须读到 EOF（FIN），否则"请求发完了但服务端还在等"的协议会被卡住
        /// </summary>
        [TestMethod]
        public async Task ClientHalfCloseSendsFinToTarget()
        {
            using var server = new TestTargetServer();
            await using var tunnel = Tunnel.Start(server.Uri);
            var payload = RandomBytes(128 * 1024, seed: 31);

            var targetTask = Task.Run(async () =>
            {
                using var target = await server.AcceptAsync(Timeout);
                return await TestTargetServer.ReadToEndAsync(target, FinTimeout);
            });

            foreach (var chunk in Chunked(payload, seed: 32))
            {
                tunnel.Send(chunk);
            }

            tunnel.CompleteClientStream();

            AssertBytesEqual(payload, await targetTask);
        }

        /// <summary>只完成客户端发送方向，完全不动目标服务器，隧道不应自行结束（半关闭后仍能收数据）</summary>
        [TestMethod]
        public async Task TunnelKeepsReceivingAfterClientHalfClose()
        {
            using var server = new TestTargetServer();
            await using var tunnel = Tunnel.Start(server.Uri);
            var response = RandomBytes(64 * 1024, seed: 33);

            using var target = await server.AcceptAsync(Timeout);
            await tunnel.ReadHandshakeAsync(Timeout);

            tunnel.CompleteClientStream();
            Assert.AreEqual(0, (await TestTargetServer.ReadToEndAsync(target, FinTimeout)).Length, "目标应收到 FIN");

            //半关闭之后，目标仍然可以继续回数据
            await TestTargetServer.WriteExactlyAsync(target, response, Timeout);
            AssertBytesEqual(response, await tunnel.ReadPayloadAsync(response.Length, Timeout));
        }

        /// <summary>
        /// 客户端发完后，目标服务器隔 6 秒（超过原来的 5 秒排空窗口）才回数据，响应必须完整送达。
        /// 这是"客户端半关闭后按固定超时强拆"的回归测试：客户端明确说了发完，目标服务器要多久都得等
        /// </summary>
        [TestMethod]
        public async Task SlowTargetResponseAfterClientHalfCloseIsNotTruncated()
        {
            using var server = new TestTargetServer();
            await using var tunnel = Tunnel.Start(server.Uri);
            var request = RandomBytes(4096, seed: 101);
            var response = RandomBytes(64 * 1024, seed: 102);

            using var target = await server.AcceptAsync(Timeout);
            await tunnel.ReadHandshakeAsync(Timeout);

            tunnel.Send(request);
            tunnel.CompleteClientStream();

            //目标服务器先收到完整请求和 FIN
            AssertBytesEqual(request, await TestTargetServer.ReadToEndAsync(target, FinTimeout));

            //超过排空窗口后再回数据
            await Task.Delay(TimeSpan.FromSeconds(6));
            await TestTargetServer.WriteExactlyAsync(target, response, Timeout);
            target.Shutdown(SocketShutdown.Send);

            AssertBytesEqual(response, await tunnel.ReadPayloadAsync(response.Length, Timeout));
            await tunnel.Task.WaitAsync(Timeout);
        }

        /// <summary>
        /// 客户端流异常时数据不完整，不能补发 FIN；也因此不能无限等目标服务器，必须在收尾窗口内退出。
        /// 若把"是否补发 FIN"的判断写错（无脑无限等），这个用例会挂住
        /// </summary>
        [TestMethod]
        public async Task ClientStreamErrorDoesNotHangTunnel()
        {
            using var server = new TestTargetServer();
            await using var tunnel = Tunnel.Start(server.Uri);
            var prefix = RandomBytes(8 * 1024, seed: 111);

            using var target = await server.AcceptAsync(Timeout); //保持打开，逼隧道走收尾窗口
            await tunnel.ReadHandshakeAsync(Timeout);

            tunnel.Send(prefix);
            tunnel.FaultClientStream(new IOException("模拟客户端流异常"));

            AssertBytesEqual(prefix, await TestTargetServer.ReadExactlyAsync(target, prefix.Length, Timeout));

            await tunnel.Task.WaitAsync(TimeSpan.FromSeconds(10));
            Assert.AreEqual(0, GlobalState.Connections.Count, "异常结束后不应残留连接记录");
        }

        // ---------------- 压力与边界 ----------------

        /// <summary>单条 4MB 消息双向转发，验证大消息不被截断/错位</summary>
        [TestMethod]
        public async Task LargeSingleMessageIsForwardedExactly()
        {
            using var server = new TestTargetServer();
            await using var tunnel = Tunnel.Start(server.Uri);
            var request = RandomBytes(4 * 1024 * 1024, seed: 121);
            var response = RandomBytes(4 * 1024 * 1024, seed: 122);

            var targetTask = Task.Run(async () =>
            {
                using var target = await server.AcceptAsync(Timeout);
                var readTask = TestTargetServer.ReadExactlyAsync(target, request.Length, Timeout);
                await TestTargetServer.WriteExactlyAsync(target, response, Timeout);
                return await readTask;
            });

            await tunnel.ReadHandshakeAsync(Timeout);

            var receiveTask = tunnel.ReadPayloadAsync(response.Length, Timeout);
            tunnel.Send(request);
            tunnel.CompleteClientStream();

            AssertBytesEqual(request, await targetTask);
            AssertBytesEqual(response, await receiveTask);
        }

        /// <summary>约 5000 条 1~7 字节的小消息，验证不乱序不丢失</summary>
        [TestMethod]
        public async Task ManySmallMessagesKeepOrder()
        {
            using var server = new TestTargetServer();
            await using var tunnel = Tunnel.Start(server.Uri);
            var payload = RandomBytes(20 * 1024, seed: 131);

            var targetTask = Task.Run(async () =>
            {
                using var target = await server.AcceptAsync(Timeout);
                return await TestTargetServer.ReadExactlyAsync(target, payload.Length, Timeout);
            });

            var random = new Random(132);
            var offset = 0;
            while (offset < payload.Length)
            {
                var size = Math.Min(random.Next(1, 8), payload.Length - offset);
                tunnel.Send(payload[offset..(offset + size)]);
                offset += size;
            }

            tunnel.CompleteClientStream();

            AssertBytesEqual(payload, await targetTask);
        }

        /// <summary>随机长度（含 0）、随机分片的双向并发压测，逐轮做字节级校验</summary>
        [TestMethod]
        public async Task RandomizedBidirectionalStress()
        {
            for (var round = 0; round < 8; round++)
            {
                using var server = new TestTargetServer();
                await using var tunnel = Tunnel.Start(server.Uri);

                var random = new Random(1000 + round);
                var request = RandomBytes(random.Next(0, 512 * 1024), seed: 2000 + round);
                var response = RandomBytes(random.Next(0, 512 * 1024), seed: 3000 + round);

                var targetTask = Task.Run(async () =>
                {
                    using var target = await server.AcceptAsync(Timeout);
                    var readTask = TestTargetServer.ReadExactlyAsync(target, request.Length, Timeout);
                    await TestTargetServer.WriteExactlyAsync(target, response, Timeout);
                    return await readTask;
                });

                await tunnel.ReadHandshakeAsync(Timeout);

                var receiveTask = tunnel.ReadPayloadAsync(response.Length, Timeout);
                foreach (var chunk in Chunked(request, seed: 4000 + round))
                {
                    tunnel.Send(chunk);
                }

                tunnel.CompleteClientStream();

                try
                {
                    AssertBytesEqual(request, await targetTask);
                    AssertBytesEqual(response, await receiveTask);
                }
                catch (Exception ex)
                {
                    //带上轮次参数，失败时能直接定位到具体形状
                    var consumed = tunnel.ConsumedByTunnel;
                    Assert.Fail($"第 {round} 轮失败（request={request.Length}, response={response.Length}, " +
                        $"隧道已读={consumed.Count}条/{consumed.Sum(x => x.Payload.Length)}字节, " +
                        $"隧道状态={tunnel.Task.Status}）：{ex.Message}");
                }
            }
        }

        /// <summary>
        /// 目标服务器先压一段时间不读，客户端发完后立刻半关闭：FIN 必须排在全部数据之后。
        /// 用"管道 + 异步发送队列"实现时，这里正是那个偶发丢尾字节的点
        /// </summary>
        [TestMethod]
        public async Task FinArrivesAfterAllDataWhenTargetReadsSlowly()
        {
            using var server = new TestTargetServer();
            await using var tunnel = Tunnel.Start(server.Uri);
            var payload = RandomBytes(512 * 1024, seed: 141);

            using var target = await server.AcceptAsync(Timeout);
            await tunnel.ReadHandshakeAsync(Timeout);

            var readTask = Task.Run(async () =>
            {
                await Task.Delay(200); //先不读，把发送侧压满，制造"发送在途"的状态
                return await TestTargetServer.ReadToEndAsync(target, Timeout);
            });

            //512KB 拆成 [512000, 11723, 565]：大分片后面紧跟小分片，正是丢尾字节的形状
            tunnel.Send(payload.AsSpan(0, 512000).ToArray());
            tunnel.Send(payload.AsSpan(512000, 11723).ToArray());
            tunnel.Send(payload.AsSpan(523723, 565).ToArray());
            tunnel.CompleteClientStream();

            //读到 EOF 时必须是完整的 512KB，而不是少了尾巴
            AssertBytesEqual(payload, await readTask);
        }

        // ---------------- 生命周期与清理 ----------------

        [TestMethod]
        public async Task TargetCloseEndsTunnel()
        {
            using var server = new TestTargetServer();
            await using var tunnel = Tunnel.Start(server.Uri);

            var target = await server.AcceptAsync(Timeout);
            await tunnel.ReadHandshakeAsync(Timeout);

            target.Dispose();
            tunnel.CompleteClientStream();

            await tunnel.Task.WaitAsync(Timeout);
            Assert.AreEqual(0, GlobalState.Connections.Count, "隧道结束后不应残留连接记录");
        }

        [TestMethod]
        public async Task ConnectionIsTrackedWhileActiveThenRemoved()
        {
            using var server = new TestTargetServer();
            await using var tunnel = Tunnel.Start(server.Uri);

            var target = await server.AcceptAsync(Timeout);
            await tunnel.ReadHandshakeAsync(Timeout);

            await WaitUntilAsync(() => GlobalState.Connections.Count == 1, Timeout);
            var item = GlobalState.Connections.Values.Single();
            StringAssert.Contains(item.ClientIpAddress, $"127.0.0.1:{server.Port}", "连接记录应包含目标地址");

            target.Dispose();
            tunnel.CompleteClientStream();
            await tunnel.Task.WaitAsync(Timeout);

            await WaitUntilAsync(() => GlobalState.Connections.IsEmpty, Timeout);
        }

        [TestMethod]
        public async Task ClientCancellationEndsTunnelWithoutThrowing()
        {
            using var server = new TestTargetServer();
            await using var tunnel = Tunnel.Start(server.Uri);

            using var target = await server.AcceptAsync(Timeout);
            await tunnel.ReadHandshakeAsync(Timeout);
            await WaitUntilAsync(() => GlobalState.Connections.Count == 1, Timeout);

            tunnel.Cancel();

            //取消属于正常收尾，方法应正常返回而不是把 OperationCanceledException 抛给 gRPC
            await tunnel.Task.WaitAsync(Timeout);
            await WaitUntilAsync(() => GlobalState.Connections.IsEmpty, Timeout);
        }

        [TestMethod]
        public async Task HostStopEndsTunnelWithoutThrowing()
        {
            using var server = new TestTargetServer();
            await using var tunnel = Tunnel.Start(server.Uri);

            using var target = await server.AcceptAsync(Timeout);
            await tunnel.ReadHandshakeAsync(Timeout);

            tunnel.Lifetime.StopApplication();

            await tunnel.Task.WaitAsync(Timeout);
        }

        // ---------------- 口令校验 ----------------

        [TestMethod]
        public async Task RemoteClientWithoutPasswordIsRejected()
        {
            using var server = new TestTargetServer();
            await using var tunnel = Tunnel.Start(server.Uri, clientIpAddress: RemoteClient);

            var exception = await Assert.ThrowsExceptionAsync<RpcException>(async () => await tunnel.Task.WaitAsync(Timeout));
            Assert.AreEqual(StatusCode.Unauthenticated, exception.Status.StatusCode);
        }

        [TestMethod]
        public async Task RemoteClientWithWrongPasswordIsRejected()
        {
            using var server = new TestTargetServer();
            await using var tunnel = Tunnel.Start(server.Uri, authorization: "Password wrong", clientIpAddress: RemoteClient);

            var exception = await Assert.ThrowsExceptionAsync<RpcException>(async () => await tunnel.Task.WaitAsync(Timeout));
            Assert.AreEqual(StatusCode.Unauthenticated, exception.Status.StatusCode);
        }

        [TestMethod]
        public async Task RemoteClientWithCorrectPasswordIsAccepted()
        {
            using var server = new TestTargetServer();
            await using var tunnel = Tunnel.Start(server.Uri, authorization: $"Password {Tunnel.Password}", clientIpAddress: RemoteClient);

            using var target = await server.AcceptAsync(Timeout);
            await tunnel.ReadHandshakeAsync(Timeout);
        }

        [TestMethod]
        public async Task LoopbackClientNeedsNoPassword()
        {
            using var server = new TestTargetServer();
            await using var tunnel = Tunnel.Start(server.Uri, clientIpAddress: IPAddress.Loopback);

            using var target = await server.AcceptAsync(Timeout);
            await tunnel.ReadHandshakeAsync(Timeout);
        }

        // ---------------- 目标地址校验 ----------------

        [DataTestMethod]
        [DataRow(null, DisplayName = "缺少 X-Requested-With")]
        [DataRow("", DisplayName = "空字符串")]
        [DataRow("   ", DisplayName = "全空白")]
        [DataRow("/relative", DisplayName = "相对地址")]
        [DataRow("not a uri", DisplayName = "不是 URI")]
        [DataRow("tcp://host", DisplayName = "未知协议且没有端口")]
        [DataRow("http://host:99999", DisplayName = "端口越界")]
        public async Task InvalidRequestedWithIsRejected(string? uri)
        {
            await using var tunnel = Tunnel.Start(uri);

            var exception = await Assert.ThrowsExceptionAsync<RpcException>(async () => await tunnel.Task.WaitAsync(Timeout));
            Assert.AreEqual(StatusCode.InvalidArgument, exception.Status.StatusCode);
        }

        [TestMethod]
        public async Task UnreachableTargetFailsWithoutTrackingConnection()
        {
            await using var tunnel = Tunnel.Start($"http://127.0.0.1:{GetClosedPort()}/");

            await Assert.ThrowsExceptionAsync<SocketException>(async () => await tunnel.Task.WaitAsync(Timeout));
            Assert.AreEqual(0, GlobalState.Connections.Count, "连接失败不应留下记录");
        }

        [TestMethod]
        public async Task Ipv6LiteralTargetIsSupported()
        {
            TestTargetServer server;
            try
            {
                server = new TestTargetServer(IPAddress.IPv6Loopback);
            }
            catch (SocketException)
            {
                Assert.Inconclusive("本机不支持 IPv6 回环");
                return;
            }

            using (server)
            {
                //DnsSafeHost 必须去掉方括号，否则会当成域名去解析
                await using var tunnel = Tunnel.Start(server.Uri);

                using var target = await server.AcceptAsync(Timeout);
                await tunnel.ReadHandshakeAsync(Timeout);
            }
        }

        // ---------------- 建连超时 ----------------

        /// <summary>
        /// 目标把监听队列塞满后不再回 SYN/ACK，连接会一直挂着。
        /// 服务端必须在自己的超时后以 Unavailable 失败，而不是只依赖客户端取消
        /// （实测过一次服务端连某个 CDN 迟迟不返回、客户端侧挂了 4 分钟）
        /// </summary>
        [TestMethod]
        public async Task 目标建连超时返回Unavailable()
        {
            using var blockedServer = new Socket(AddressFamily.InterNetwork, SocketType.Stream, ProtocolType.Tcp);
            blockedServer.Bind(new IPEndPoint(IPAddress.Loopback, 0));
            blockedServer.Listen(1);
            var port = ((IPEndPoint)blockedServer.LocalEndPoint!).Port;

            var blockers = new List<Socket>();
            try
            {
                //塞满 accept 队列：后续 connect 会一直等，不会成功也不会被拒
                for (var i = 0; i < 4; i++)
                {
                    var socket = new Socket(AddressFamily.InterNetwork, SocketType.Stream, ProtocolType.Tcp);
                    try
                    {
                        await socket.ConnectAsync(new IPEndPoint(IPAddress.Loopback, port)).WaitAsync(TimeSpan.FromSeconds(2));
                        blockers.Add(socket);
                    }
                    catch
                    {
                        socket.Dispose();
                    }
                }

                await using var tunnel = Tunnel.Start($"http://127.0.0.1:{port}/", connectTimeout: TimeSpan.FromSeconds(1));

                var stopwatch = Stopwatch.StartNew();
                var exception = await Assert.ThrowsExceptionAsync<RpcException>(
                    async () => await tunnel.Task.WaitAsync(TimeSpan.FromSeconds(15)));
                stopwatch.Stop();

                Assert.AreEqual(StatusCode.Unavailable, exception.Status.StatusCode, "建连超时应返回 Unavailable（可重试）");
                Assert.IsTrue(stopwatch.Elapsed < TimeSpan.FromSeconds(12),
                    $"应当在自己的超时后尽快失败，实际 {stopwatch.Elapsed.TotalSeconds:F1}s");
            }
            finally
            {
                foreach (var socket in blockers)
                {
                    socket.Dispose();
                }
            }
        }

        /// <summary>客户端主动取消不能被当成建连超时（否则会把取消误报成 Unavailable）</summary>
        [TestMethod]
        public async Task 客户端取消不算建连超时()
        {
            using var blockedServer = new Socket(AddressFamily.InterNetwork, SocketType.Stream, ProtocolType.Tcp);
            blockedServer.Bind(new IPEndPoint(IPAddress.Loopback, 0));
            blockedServer.Listen(1);
            var port = ((IPEndPoint)blockedServer.LocalEndPoint!).Port;

            var blockers = new List<Socket>();
            try
            {
                for (var i = 0; i < 4; i++)
                {
                    var socket = new Socket(AddressFamily.InterNetwork, SocketType.Stream, ProtocolType.Tcp);
                    try
                    {
                        await socket.ConnectAsync(new IPEndPoint(IPAddress.Loopback, port)).WaitAsync(TimeSpan.FromSeconds(2));
                        blockers.Add(socket);
                    }
                    catch
                    {
                        socket.Dispose();
                    }
                }

                //超时设得很长，保证失败只可能来自客户端取消
                await using var tunnel = Tunnel.Start($"http://127.0.0.1:{port}/", connectTimeout: TimeSpan.FromMinutes(5));

                await Task.Delay(300);
                tunnel.Cancel();

                await Assert.ThrowsExceptionAsync<OperationCanceledException>(
                    async () => await tunnel.Task.WaitAsync(TimeSpan.FromSeconds(10)));
            }
            finally
            {
                foreach (var socket in blockers)
                {
                    socket.Dispose();
                }
            }
        }

        // ---------------- 辅助 ----------------

        private static byte[] RandomBytes(int length, int seed)
        {
            var buffer = new byte[length];
            new Random(seed).NextBytes(buffer);
            return buffer;
        }

        /// <summary>按随机分片大小切分，覆盖 1 字节、跨帧等边界</summary>
        private static IEnumerable<byte[]> Chunked(byte[] data, int seed)
        {
            var random = new Random(seed);
            var offset = 0;
            while (offset < data.Length)
            {
                var size = Math.Min(random.Next(1, 200 * 1024), data.Length - offset);
                var chunk = new byte[size];
                Array.Copy(data, offset, chunk, 0, size);
                offset += size;
                yield return chunk;
            }
        }

        private static void AssertBytesEqual(byte[] expected, byte[] actual)
        {
            Assert.AreEqual(expected.Length, actual.Length, "字节数不一致");
            for (var i = 0; i < expected.Length; i++)
            {
                if (expected[i] != actual[i])
                {
                    Assert.Fail($"第 {i} 字节不一致：期望 0x{expected[i]:X2}，实际 0x{actual[i]:X2}");
                }
            }
        }

        private static async Task WaitUntilAsync(Func<bool> condition, TimeSpan timeout)
        {
            var deadline = DateTime.UtcNow + timeout;
            while (DateTime.UtcNow < deadline)
            {
                if (condition())
                {
                    return;
                }

                await Task.Delay(20);
            }

            Assert.Fail("等待条件超时");
        }

        private static int GetClosedPort()
        {
            using var listener = new Socket(AddressFamily.InterNetwork, SocketType.Stream, ProtocolType.Tcp);
            listener.Bind(new IPEndPoint(IPAddress.Loopback, 0));
            return ((IPEndPoint)listener.LocalEndPoint!).Port;
        }
    }
}
