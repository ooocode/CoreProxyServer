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
