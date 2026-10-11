using CoreProxy.Server.Orleans.Models;
using CoreProxy.Server.Orleans.Services;
using Google.Protobuf;
using Grpc.Core;
using Hello;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Net.Http.Headers;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using System.Net;
using System.Threading.Channels;

namespace CoreProxy.Server.Orleans.Tests.Infrastructure
{
    /// <summary>
    /// 把 StreamHandler 与假的双向流、假的 ServerCallContext、真实的 Kestrel socket 传输装配在一起。
    /// </summary>
    public sealed class Tunnel : IAsyncDisposable
    {
        /// <summary>与 Program.cs 生成的口令同构：证书原始数据的 SHA-256 十六进制串</summary>
        public const string Password = "5e884898da28047151d0e56f8dc6292773603d0d6aabbdd62a11ef721d1542d8";

        private readonly CancellationTokenSource contextSource = new();
        private readonly Channel<HttpData> clientChannel = Channel.CreateUnbounded<HttpData>();
        private readonly ChannelStreamReader<HttpData> clientReader;
        private readonly Channel<HttpData> responseChannel = Channel.CreateUnbounded<HttpData>();

        private Tunnel(string? uri, string? authorization, IPAddress clientIpAddress, TestHostApplicationLifetime lifetime,
            TimeSpan? connectTimeout)
        {
            var configuration = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["ConnectTimeoutSeconds"] = ((int)(connectTimeout ?? TimeSpan.FromSeconds(20)).TotalSeconds).ToString(),
            }).Build();

            Lifetime = lifetime;

            var headers = new Metadata();
            if (uri is not null)
            {
                headers.Add(HeaderNames.XRequestedWith, uri);
            }

            if (authorization is not null)
            {
                headers.Add(HeaderNames.Authorization, authorization);
            }

            var context = new TestServerCallContext(headers, contextSource.Token, clientIpAddress);
            var service = new MyGrpcService(
                lifetime,
                new CertificatePassword { Password = Password },
                NullLogger<MyGrpcService>.Instance,
                hubContext: null!, //StreamHandler 不涉及 SignalR
                configuration);

            clientReader = new ChannelStreamReader<HttpData>(clientChannel.Reader);
            Task = service.StreamHandler(
                clientReader,
                new HttpDataStreamWriter(responseChannel.Writer, contextSource.Token),
                context);

            //隧道结束后让测试侧的读取循环自然结束
            _ = Task.ContinueWith(_ => responseChannel.Writer.TryComplete(), TaskScheduler.Default);
        }

        public static Tunnel Start(string? uri, string? authorization = null, IPAddress? clientIpAddress = null,
            TestHostApplicationLifetime? lifetime = null, TimeSpan? connectTimeout = null) =>
            new(uri, authorization, clientIpAddress ?? IPAddress.Loopback, lifetime ?? new TestHostApplicationLifetime(), connectTimeout);

        public Task Task { get; }

        /// <summary>诊断用：生产代码实际从请求流里读走了哪些消息</summary>
        public IReadOnlyList<HttpData> ConsumedByTunnel => clientReader.Consumed;

        public TestHostApplicationLifetime Lifetime { get; }

        /// <summary>模拟客户端发出的数据分片</summary>
        public void Send(byte[] data) => clientChannel.Writer.TryWrite(new HttpData { Payload = ByteString.CopyFrom(data) });

        /// <summary>客户端发送方向结束（半关闭）</summary>
        public void CompleteClientStream() => clientChannel.Writer.TryComplete();

        /// <summary>客户端请求流以异常结束（等价于 gRPC 请求流中断）</summary>
        public void FaultClientStream(Exception error) => clientChannel.Writer.TryComplete(error);

        /// <summary>取消 RPC，等价于客户端断开</summary>
        public void Cancel() => contextSource.Cancel();

        /// <summary>读取并校验第一个响应（表示连接成功的空包）</summary>
        public async Task<HttpData> ReadHandshakeAsync(TimeSpan timeout)
        {
            var handshake = await ReadMessageAsync(timeout);
            Assert.AreEqual(0, handshake.Payload.Length, "第一个响应必须是表示连接成功的空包");
            Assert.IsTrue(handshake.UnixTimeMilliseconds > 0, "握手包应带上时间戳");
            return handshake;
        }

        public async Task<HttpData> ReadMessageAsync(TimeSpan timeout)
        {
            try
            {
                return await responseChannel.Reader.ReadAsync(CancellationToken.None).AsTask().WaitAsync(timeout);
            }
            catch (ChannelClosedException) when (Task.Exception is not null)
            {
                //把隧道内部的真实异常带出来，否则只剩"channel has been closed"
                throw new AssertFailedException($"隧道已结束：{Task.Exception.GetBaseException()}", Task.Exception.GetBaseException());
            }
        }

        /// <summary>累计读到指定字节数（不含握手包，调用前需先消费握手）</summary>
        public async Task<byte[]> ReadPayloadAsync(int count, TimeSpan timeout)
        {
            using var stream = new MemoryStream(count);
            while (stream.Length < count)
            {
                var message = await ReadMessageAsync(timeout);
                stream.Write(message.Payload.Span);
            }

            return stream.ToArray();
        }

        /// <summary>隧道结束后，响应流里不应再有多余数据</summary>
        public void AssertNoMoreData() => Assert.IsFalse(responseChannel.Reader.TryRead(out _), "响应流中存在多余数据");

        public async ValueTask DisposeAsync()
        {
            if (!contextSource.IsCancellationRequested)
            {
                contextSource.Cancel();
            }

            try
            {
                await Task.WaitAsync(TimeSpan.FromSeconds(5));
            }
            catch
            {
                //清理阶段忽略隧道自身的异常，异常断言由用例显式完成
            }

            contextSource.Dispose();
        }
    }
}
