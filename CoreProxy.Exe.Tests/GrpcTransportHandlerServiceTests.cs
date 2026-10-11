using CoreProxy.BackgroundServices;
using CoreProxy.Exe.Tests.Infrastructure;
using CoreProxy.Internal;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace CoreProxy.Exe.Tests
{
    /// <summary>客户端数据面：浏览器与服务器之间的双向转发（用假 CallInvoker，全程不出网）</summary>
    [TestClass]
    public sealed class GrpcTransportHandlerServiceTests
    {
        private static readonly TimeSpan Timeout = TimeSpan.FromSeconds(20);

        private static GrpcTransportHandlerService CreateService() =>
            new(NullLogger<GrpcTransportHandlerService>.Instance);

        // ---------------- 成功应答 ----------------

        [TestMethod]
        public async Task SOCKS5类型回SOCKS5成功应答()
        {
            var tunnel = new FakeTunnel();
            var context = new TestConnectionContext();
            using var cts = new CancellationTokenSource(Timeout);
            using var tokenSource = new CancellationTokenSource();

            var task = CreateService().DualStreamHandlerAsync(tunnel.Client, context.Transport, "tcp://example.com:443", ProxyType.Socket5, tokenSource);
            tunnel.SendHandshake();

            ClientTestHelpers.AssertBytesEqual(ClientTestHelpers.ConnectOk,
                await TestPipeline.ReadExactlyAsync(context.BrowserReader, ClientTestHelpers.ConnectOk.Length, cts.Token));

            tunnel.CompleteServerStream();
            context.CompleteBrowser();
            await task.WaitAsync(Timeout);
        }

        [TestMethod]
        public async Task HTTP类型回HTTP成功应答()
        {
            var tunnel = new FakeTunnel();
            var context = new TestConnectionContext();
            using var cts = new CancellationTokenSource(Timeout);
            using var tokenSource = new CancellationTokenSource();

            var task = CreateService().DualStreamHandlerAsync(tunnel.Client, context.Transport, "tcp://example.com:443", ProxyType.Http, tokenSource);
            tunnel.SendHandshake();

            ClientTestHelpers.AssertBytesEqual(ClientTestHelpers.HttpConnectOk,
                await TestPipeline.ReadExactlyAsync(context.BrowserReader, ClientTestHelpers.HttpConnectOk.Length, cts.Token));

            tunnel.CompleteServerStream();
            context.CompleteBrowser();
            await task.WaitAsync(Timeout);
        }

        /// <summary>服务器连第一个消息都没回就结束（例如目标不可达），浏览器不应该收到"成功"应答</summary>
        [TestMethod]
        public async Task 服务器未回第一个消息就结束则不应答()
        {
            var tunnel = new FakeTunnel();
            var context = new TestConnectionContext();
            using var cts = new CancellationTokenSource(Timeout);
            using var tokenSource = new CancellationTokenSource();

            tunnel.CompleteServerStream();

            var task = CreateService().DualStreamHandlerAsync(tunnel.Client, context.Transport, "tcp://example.com:443", ProxyType.Socket5, tokenSource);
            context.CompleteBrowser();
            await task.WaitAsync(Timeout);

            context.CompleteProxyOutput();
            var received = await TestPipeline.ReadAllAsync(context.BrowserReader, cts.Token);
            Assert.AreEqual(0, received.Length, "不应答任何字节");
        }

        // ---------------- 字节完整性 ----------------

        [TestMethod]
        public async Task 浏览器到服务器字节精确()
        {
            var tunnel = new FakeTunnel();
            var context = new TestConnectionContext();
            using var cts = new CancellationTokenSource(Timeout);
            using var tokenSource = new CancellationTokenSource();

            var task = CreateService().DualStreamHandlerAsync(tunnel.Client, context.Transport, "tcp://example.com:443", ProxyType.Socket5, tokenSource);
            tunnel.SendHandshake();
            await TestPipeline.ReadExactlyAsync(context.BrowserReader, ClientTestHelpers.ConnectOk.Length, cts.Token);

            var payload = ClientTestHelpers.RandomBytes(2 * 1024 * 1024, seed: 1);
            var receivedTask = ClientTestHelpers.ReadMessagesAsync(tunnel.RequestStream.Reader, payload.Length, cts.Token);

            foreach (var chunk in ClientTestHelpers.Chunked(payload, seed: 2))
            {
                await context.BrowserWriter.WriteAsync(chunk, cts.Token);
            }

            context.CompleteBrowser();
            tunnel.CompleteServerStream();

            ClientTestHelpers.AssertBytesEqual(payload, await receivedTask);
            await task.WaitAsync(Timeout);
        }

        [TestMethod]
        public async Task 服务器到浏览器字节精确()
        {
            var tunnel = new FakeTunnel();
            var context = new TestConnectionContext();
            using var cts = new CancellationTokenSource(Timeout);
            using var tokenSource = new CancellationTokenSource();

            var task = CreateService().DualStreamHandlerAsync(tunnel.Client, context.Transport, "tcp://example.com:443", ProxyType.Socket5, tokenSource);
            tunnel.SendHandshake();
            await TestPipeline.ReadExactlyAsync(context.BrowserReader, ClientTestHelpers.ConnectOk.Length, cts.Token);

            var payload = ClientTestHelpers.RandomBytes(2 * 1024 * 1024, seed: 3);

            //必须边喂边读：浏览器侧不读的话，客户端会被背压顶住（这是正确行为），
            //再叠加收尾的 5 秒兜底就把传输截断了
            var receivedTask = TestPipeline.ReadExactlyAsync(context.BrowserReader, payload.Length, cts.Token);

            foreach (var chunk in ClientTestHelpers.Chunked(payload, seed: 4))
            {
                tunnel.SendData(chunk);
            }

            tunnel.CompleteServerStream();
            context.CompleteBrowser();

            ClientTestHelpers.AssertBytesEqual(payload, await receivedTask);
            await task.WaitAsync(Timeout);
        }

        [TestMethod]
        public async Task 双向并发转发不丢不乱序()
        {
            var tunnel = new FakeTunnel();
            var context = new TestConnectionContext();
            using var cts = new CancellationTokenSource(Timeout);
            using var tokenSource = new CancellationTokenSource();

            var request = ClientTestHelpers.RandomBytes(2 * 1024 * 1024, seed: 5);
            var response = ClientTestHelpers.RandomBytes(2 * 1024 * 1024, seed: 6);

            var task = CreateService().DualStreamHandlerAsync(tunnel.Client, context.Transport, "tcp://example.com:443", ProxyType.Socket5, tokenSource);
            tunnel.SendHandshake();
            await TestPipeline.ReadExactlyAsync(context.BrowserReader, ClientTestHelpers.ConnectOk.Length, cts.Token);

            var receivedRequestTask = ClientTestHelpers.ReadMessagesAsync(tunnel.RequestStream.Reader, request.Length, cts.Token);
            var receivedResponseTask = Task.Run(async () =>
            {
                var bytes = await TestPipeline.ReadExactlyAsync(context.BrowserReader, response.Length, cts.Token);
                return bytes;
            });

            foreach (var chunk in ClientTestHelpers.Chunked(response, seed: 7))
            {
                tunnel.SendData(chunk);
            }

            foreach (var chunk in ClientTestHelpers.Chunked(request, seed: 8))
            {
                await context.BrowserWriter.WriteAsync(chunk, cts.Token);
            }

            ClientTestHelpers.AssertBytesEqual(request, await receivedRequestTask);
            ClientTestHelpers.AssertBytesEqual(response, await receivedResponseTask);

            context.CompleteBrowser();
            tunnel.CompleteServerStream();
            await task.WaitAsync(Timeout);
        }

        // ---------------- 半关闭与截断 ----------------

        /// <summary>
        /// 浏览器发完后服务器隔 6 秒（超过旧的 5 秒收尾窗口）才回数据，响应必须完整送达。
        /// 这是"浏览器发完就按固定超时强拆"的回归测试
        /// </summary>
        [TestMethod]
        public async Task 浏览器发完后长响应不被截断()
        {
            var tunnel = new FakeTunnel();
            var context = new TestConnectionContext();
            using var cts = new CancellationTokenSource(Timeout);
            using var tokenSource = new CancellationTokenSource();

            var task = CreateService().DualStreamHandlerAsync(tunnel.Client, context.Transport, "tcp://example.com:443", ProxyType.Socket5, tokenSource);
            tunnel.SendHandshake();
            await TestPipeline.ReadExactlyAsync(context.BrowserReader, ClientTestHelpers.ConnectOk.Length, cts.Token);

            await context.BrowserWriter.WriteAsync(ClientTestHelpers.RandomBytes(1024, seed: 21), cts.Token);
            context.CompleteBrowser();

            //超过旧的 5 秒收尾窗口后才回数据
            await Task.Delay(TimeSpan.FromSeconds(6));

            var response = ClientTestHelpers.RandomBytes(64 * 1024, seed: 22);
            var receivedTask = TestPipeline.ReadExactlyAsync(context.BrowserReader, response.Length, cts.Token);
            foreach (var chunk in ClientTestHelpers.Chunked(response, seed: 23))
            {
                tunnel.SendData(chunk);
            }

            tunnel.CompleteServerStream();

            ClientTestHelpers.AssertBytesEqual(response, await receivedTask);
            await task.WaitAsync(Timeout);
        }

        /// <summary>
        /// 浏览器发完的那一刻就要向服务器半关闭，而不是等收尾窗口结束。
        /// 旧实现把 CompleteAsync 放在 finally，服务器要等 5 秒才知道请求发完
        /// </summary>
        [TestMethod]
        public async Task 浏览器发完立刻向服务器半关闭()
        {
            var tunnel = new FakeTunnel();
            var context = new TestConnectionContext();
            using var cts = new CancellationTokenSource(Timeout);
            using var tokenSource = new CancellationTokenSource();

            var task = CreateService().DualStreamHandlerAsync(tunnel.Client, context.Transport, "tcp://example.com:443", ProxyType.Socket5, tokenSource);
            tunnel.SendHandshake();
            await TestPipeline.ReadExactlyAsync(context.BrowserReader, ClientTestHelpers.ConnectOk.Length, cts.Token);

            await context.BrowserWriter.WriteAsync(ClientTestHelpers.RandomBytes(1024, seed: 24), cts.Token);
            context.CompleteBrowser();

            //服务器侧一直保持打开：旧实现必须等满 5 秒兜底才会补 FIN
            var stopwatch = System.Diagnostics.Stopwatch.StartNew();
            await ClientTestHelpers.WaitUntilAsync(() => tunnel.RequestStream.Completed, TimeSpan.FromSeconds(3));
            Assert.IsTrue(stopwatch.ElapsedMilliseconds < 2000,
                $"半关闭应立刻发生，实际耗时 {stopwatch.ElapsedMilliseconds}ms");

            tunnel.CompleteServerStream();
            await task.WaitAsync(Timeout);
        }

        // ---------------- 收尾与资源 ----------------

        [TestMethod]
        public async Task 结束时完成请求流实现半关闭()
        {
            var tunnel = new FakeTunnel();
            var context = new TestConnectionContext();
            using var cts = new CancellationTokenSource(Timeout);
            using var tokenSource = new CancellationTokenSource();

            var task = CreateService().DualStreamHandlerAsync(tunnel.Client, context.Transport, "tcp://example.com:443", ProxyType.Socket5, tokenSource);
            tunnel.SendHandshake();
            await TestPipeline.ReadExactlyAsync(context.BrowserReader, ClientTestHelpers.ConnectOk.Length, cts.Token);

            context.CompleteBrowser();
            tunnel.CompleteServerStream();
            await task.WaitAsync(Timeout);

            Assert.IsTrue(tunnel.RequestStream.Completed, "收尾时必须完成请求流（向服务器半关闭）");
        }

        [TestMethod]
        public async Task 服务器流异常时正常返回且计数归零()
        {
            var tunnel = new FakeTunnel();
            var context = new TestConnectionContext();
            using var cts = new CancellationTokenSource(Timeout);
            using var tokenSource = new CancellationTokenSource();

            var clientBefore = Stat.ClientCount.Value;
            var serverBefore = Stat.ServerCount.Value;

            var task = CreateService().DualStreamHandlerAsync(tunnel.Client, context.Transport, "tcp://example.com:443", ProxyType.Socket5, tokenSource);
            tunnel.SendHandshake();
            await TestPipeline.ReadExactlyAsync(context.BrowserReader, ClientTestHelpers.ConnectOk.Length, cts.Token);

            tunnel.ServerMessages.Writer.TryComplete(new IOException("模拟服务器流异常"));
            context.CompleteBrowser();

            await task.WaitAsync(Timeout);

            Assert.AreEqual(clientBefore, Stat.ClientCount.Value, "客户端侧计数未归零");
            Assert.AreEqual(serverBefore, Stat.ServerCount.Value, "服务器侧计数未归零");
        }
    }
}
