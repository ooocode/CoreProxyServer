using CoreProxy.Exe.Tests.Infrastructure;
using CoreProxy.Internal;
using CoreProxy.ViewModels;
using Microsoft.AspNetCore.Connections;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using System.Text;

namespace CoreProxy.Exe.Tests
{
    /// <summary>连接中间件：探嗅 SOCKS5 / HTTP 分流，并驱动 SOCKS5 全流程（内存管道 + 假隧道）</summary>
    [TestClass]
    public sealed class Socket5ConnectionHandlerServiceTests
    {
        private const int LocalPort = 1080;
        private static readonly TimeSpan Timeout = TimeSpan.FromSeconds(20);

        // ---------------- 分流判定 ----------------

        [TestMethod]
        public async Task HTTP请求交给后续中间件且字节不被消费()
        {
            var (service, _) = CreateService(new FakeTunnel(), new TestHostApplicationLifetime());
            var context = new TestConnectionContext(LocalPort);
            using var cts = new CancellationTokenSource(Timeout);

            var request = Encoding.ASCII.GetBytes("GET /pac.txt HTTP/1.1\r\nHost: localhost\r\n\r\n");
            await context.BrowserWriter.WriteAsync(request, cts.Token);
            context.CompleteBrowser();

            var nextCalled = false;
            byte[]? seenByNext = null;
            ConnectionDelegate next = async connection =>
            {
                nextCalled = true;
                seenByNext = await TestPipeline.ReadAllAsync(connection.Transport.Input, cts.Token);
            };

            await service.OnConnectedAsync(context, next).WaitAsync(Timeout);

            Assert.IsTrue(nextCalled, "HTTP 请求应交给后续中间件");
            ClientTestHelpers.AssertBytesEqual(request, seenByNext!, "探嗅不能消费 HTTP 请求的字节");
        }

        /// <summary>HTTPS（TLS ClientHello）也必须走 HTTP 分支，并把原始字节完整交给后续中间件</summary>
        [TestMethod]
        public async Task TLS握手也走HTTP分支且字节不被消费()
        {
            var (service, _) = CreateService(new FakeTunnel(), new TestHostApplicationLifetime());
            var context = new TestConnectionContext(LocalPort);
            using var cts = new CancellationTokenSource(Timeout);

            //典型的 TLS ClientHello 开头
            var clientHello = new byte[]
            {
                0x16, 0x03, 0x01, 0x00, 0x2F, 0x01, 0x00, 0x00, 0x2B, 0x03, 0x03,
            }.Concat(ClientTestHelpers.RandomBytes(48, seed: 9)).ToArray();
            await context.BrowserWriter.WriteAsync(clientHello, cts.Token);
            context.CompleteBrowser();

            var nextCalled = false;
            byte[]? seenByNext = null;
            ConnectionDelegate next = async connection =>
            {
                nextCalled = true;
                seenByNext = await TestPipeline.ReadAllAsync(connection.Transport.Input, cts.Token);
            };

            await service.OnConnectedAsync(context, next).WaitAsync(Timeout);

            Assert.IsTrue(nextCalled, "TLS 握手应交给后续中间件");
            ClientTestHelpers.AssertBytesEqual(clientHello, seenByNext!, "TLS 字节必须原样保留");
        }

        /// <summary>
        /// 首包只到了 1 个字节时必须继续等，不能立刻判定。
        /// 注意：这条用例不区分新旧实现（Kestrel 解析器在数据不完整时不抛异常，旧代码也走对了分支），
        /// 它锁定的是"至少要 2 字节才判定"这条规则本身
        /// </summary>
        [TestMethod]
        public async Task TLS首包被拆成单字节时仍走HTTP分支()
        {
            var (service, _) = CreateService(new FakeTunnel(), new TestHostApplicationLifetime());
            var context = new TestConnectionContext(LocalPort);
            using var cts = new CancellationTokenSource(Timeout);

            var clientHello = new byte[] { 0x16, 0x03, 0x01, 0x00, 0x2F, 0x01, 0x00, 0x00, 0x2B, 0x03, 0x03 }
                .Concat(ClientTestHelpers.RandomBytes(48, seed: 31)).ToArray();

            //先只发第 1 个字节，确保服务读到的是"只有 1 字节"的状态
            await context.BrowserWriter.WriteAsync(clientHello.AsMemory(0, 1), cts.Token);
            await Task.Delay(200);
            await context.BrowserWriter.WriteAsync(clientHello.AsMemory(1), cts.Token);
            context.CompleteBrowser();

            var nextCalled = false;
            byte[]? seenByNext = null;
            ConnectionDelegate next = async connection =>
            {
                nextCalled = true;
                seenByNext = await TestPipeline.ReadAllAsync(connection.Transport.Input, cts.Token);
            };

            await service.OnConnectedAsync(context, next).WaitAsync(Timeout);

            Assert.IsTrue(nextCalled, "TLS 握手必须交给后续中间件，而不是被当成 SOCKS5 丢掉");
            ClientTestHelpers.AssertBytesEqual(clientHello, seenByNext!, "TLS 字节必须原样保留");
        }

        [TestMethod]
        public async Task 非socket5握手不回复任何字节()
        {
            var (service, _) = CreateService(new FakeTunnel(), new TestHostApplicationLifetime());
            var context = new TestConnectionContext(LocalPort);
            using var cts = new CancellationTokenSource(Timeout);

            await context.BrowserWriter.WriteAsync(new byte[] { 0x04, 0x01, 0x00 }, cts.Token);
            context.CompleteBrowser();

            await service.OnConnectedAsync(context, _ => throw new InvalidOperationException("不应走 HTTP 分支")).WaitAsync(Timeout);

            context.CompleteProxyOutput();
            var received = await TestPipeline.ReadAllAsync(context.BrowserReader, cts.Token);
            Assert.AreEqual(0, received.Length, "非 SOCKS5 不应有任何应答");
        }

        [TestMethod]
        public async Task NMETHODS为0不回复任何字节()
        {
            var (service, _) = CreateService(new FakeTunnel(), new TestHostApplicationLifetime());
            var context = new TestConnectionContext(LocalPort);
            using var cts = new CancellationTokenSource(Timeout);

            await context.BrowserWriter.WriteAsync(new byte[] { 0x05, 0x00 }, cts.Token);
            context.CompleteBrowser();

            await service.OnConnectedAsync(context, _ => throw new InvalidOperationException("不应走 HTTP 分支")).WaitAsync(Timeout);

            context.CompleteProxyOutput();
            var received = await TestPipeline.ReadAllAsync(context.BrowserReader, cts.Token);
            Assert.AreEqual(0, received.Length, "NMETHODS=0 不应有任何应答");
        }

        // ---------------- 完整 SOCKS5 流程 ----------------

        [TestMethod]
        public async Task 完整SOCKS5流程_方法应答_目标下发_双向转发()
        {
            var lifetime = new TestHostApplicationLifetime();
            var tunnel = new FakeTunnel();
            var (service, factory) = CreateService(tunnel, lifetime);
            var context = new TestConnectionContext(LocalPort);
            using var cts = new CancellationTokenSource(Timeout);

            //浏览器：握手 + CONNECT example.com:443
            await context.BrowserWriter.WriteAsync(ClientTestHelpers.Greeting(), cts.Token);
            await context.BrowserWriter.WriteAsync(ClientTestHelpers.ConnectRequest("example.com", 443), cts.Token);

            var task = service.OnConnectedAsync(context, _ => throw new InvalidOperationException("不应走 HTTP 分支"));

            //1) 方法选择应答
            ClientTestHelpers.AssertBytesEqual(ClientTestHelpers.MethodSelectOk,
                await TestPipeline.ReadExactlyAsync(context.BrowserReader, ClientTestHelpers.MethodSelectOk.Length, cts.Token));

            //2) 目标地址通过 x-requested-with 传给服务器，并且按本地端口选中了对应的 serverItem
            await ClientTestHelpers.WaitUntilAsync(() => tunnel.RequestedWith is not null, Timeout);
            Assert.AreEqual("tcp://example.com:443", tunnel.RequestedWith);
            Assert.AreEqual(LocalPort.ToString(), factory.RequestedNames.Single());

            //3) 服务器回第一个消息后，浏览器才收到 SOCKS5 成功应答
            tunnel.SendHandshake();
            ClientTestHelpers.AssertBytesEqual(ClientTestHelpers.ConnectOk,
                await TestPipeline.ReadExactlyAsync(context.BrowserReader, ClientTestHelpers.ConnectOk.Length, cts.Token));

            //4) 双向数据
            var payload = Encoding.ASCII.GetBytes("hello from browser");
            await context.BrowserWriter.WriteAsync(payload, cts.Token);
            ClientTestHelpers.AssertBytesEqual(payload,
                await ClientTestHelpers.ReadMessagesAsync(tunnel.RequestStream.Reader, payload.Length, cts.Token));

            var response = Encoding.ASCII.GetBytes("hello from server");
            tunnel.SendData(response);
            ClientTestHelpers.AssertBytesEqual(response,
                await TestPipeline.ReadExactlyAsync(context.BrowserReader, response.Length, cts.Token));

            //5) 浏览器关闭后收尾
            context.CompleteBrowser();
            tunnel.CompleteServerStream();
            await task.WaitAsync(Timeout);

            Assert.IsTrue(tunnel.RequestStream.Completed, "收尾时应向服务器半关闭");
        }

        // ---------------- 辅助 ----------------

        private static (Socket5ConnectionHandlerService Service, FakeGrpcClientFactory Factory) CreateService(
            FakeTunnel tunnel, TestHostApplicationLifetime lifetime)
        {
            var factory = new FakeGrpcClientFactory(_ => tunnel.Client);
            var grpcHandler = new GrpcTransportHandlerService(NullLogger<GrpcTransportHandlerService>.Instance);
            var serverItems = new List<ServerItem> { ServerItem.Create(LocalPort, "https://proxy.invalid", "password") };

            var service = new Socket5ConnectionHandlerService(
                factory,
                grpcHandler,
                lifetime,
                serverItems,
                null!, //SignalR 分支在代码里被注释掉了，不会用到
                NullLogger<Socket5ConnectionHandlerService>.Instance);

            return (service, factory);
        }
    }
}
