using Microsoft.AspNetCore.Connections;
using Microsoft.AspNetCore.Connections.Features;
using Microsoft.AspNetCore.Http.Features;
using System.IO.Pipelines;
using System.Net;

namespace CoreProxy.Exe.Tests.Infrastructure
{
    /// <summary>
    /// 内存管道支撑的 ConnectionContext，替代真实 socket：
    /// Input  = 浏览器发来的字节，Output = 转发回浏览器的字节。
    /// </summary>
    public sealed class TestConnectionContext : ConnectionContext
    {
        private readonly Pipe browserToProxy = new();
        private readonly Pipe proxyToBrowser = new();

        public TestConnectionContext(int localPort = 1080)
        {
            LocalEndPoint = new IPEndPoint(IPAddress.Loopback, localPort);
            RemoteEndPoint = new IPEndPoint(IPAddress.Loopback, 50000);
            Transport = new DuplexPipe(browserToProxy.Reader, proxyToBrowser.Writer);
        }

        /// <summary>测试侧写入：模拟浏览器发出的字节</summary>
        public PipeWriter BrowserWriter => browserToProxy.Writer;

        /// <summary>测试侧读取：模拟浏览器收到的字节</summary>
        public PipeReader BrowserReader => proxyToBrowser.Reader;

        /// <summary>结束"代理→浏览器"方向，使测试侧的读取循环自然结束</summary>
        public void CompleteProxyOutput() => proxyToBrowser.Writer.Complete();

        /// <summary>结束"浏览器→代理"方向，模拟浏览器关闭发送方向</summary>
        public void CompleteBrowser() => browserToProxy.Writer.Complete();

        public override IDuplexPipe Transport { get; set; }

        public override string ConnectionId { get; set; } = Guid.CreateVersion7().ToString("N");

        public override IFeatureCollection Features { get; } = new FeatureCollection();

        public override IDictionary<object, object?> Items { get; set; } = new Dictionary<object, object?>();

        public override EndPoint? LocalEndPoint { get; set; }

        public override EndPoint? RemoteEndPoint { get; set; }

        public override CancellationToken ConnectionClosed { get; set; }

        public override void Abort(ConnectionAbortedException abortReason) { }

        private sealed class DuplexPipe(PipeReader input, PipeWriter output) : IDuplexPipe
        {
            public PipeReader Input { get; } = input;

            public PipeWriter Output { get; } = output;
        }
    }
}
