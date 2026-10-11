using Google.Protobuf;
using Grpc.Core;
using Grpc.Net.ClientFactory;
using Hello;
using System.Threading.Channels;

namespace CoreProxy.Exe.Tests.Infrastructure
{
    /// <summary>浏览器→服务器方向：记录客户端写出去的消息（模拟 IClientStreamWriter）</summary>
    public sealed class RecordingClientStreamWriter<T> : IClientStreamWriter<T>
    {
        private readonly Channel<T> channel = Channel.CreateUnbounded<T>();

        public ChannelReader<T> Reader => channel.Reader;

        public int Count { get; private set; }

        public bool Completed { get; private set; }

        public WriteOptions? WriteOptions { get; set; }

        public Task WriteAsync(T message) => WriteAsync(message, CancellationToken.None);

        /// <summary>
        /// 必须实现带 token 的重载：Grpc.Core 的默认接口实现会抛
        /// NotSupportedException("Cancellation of stream writes is not supported...")，而被测代码用的正是这个重载
        /// </summary>
        public Task WriteAsync(T message, CancellationToken cancellationToken)
        {
            if (cancellationToken.IsCancellationRequested)
            {
                return Task.FromCanceled(cancellationToken);
            }

            //被测代码用 UnsafeByteOperations.UnsafeWrap 直接引用管道内存，该内存会被复用；
            //真实 gRPC 在 WriteAsync 内部立即序列化，所以这里也必须当场拷贝，否则测试读到被覆盖的数据
            var stored = message is HttpData data
                ? (T)(object)new HttpData
                {
                    Payload = ByteString.CopyFrom(data.Payload.Span),
                }
                : message;

            Count++;
            return channel.Writer.WriteAsync(stored, CancellationToken.None).AsTask();
        }

        public Task CompleteAsync()
        {
            Completed = true;
            channel.Writer.TryComplete();
            return Task.CompletedTask;
        }
    }

    /// <summary>服务器→浏览器方向：测试侧往 Channel 里喂消息</summary>
    public sealed class ChannelServerStreamReader<T>(ChannelReader<T> reader) : IAsyncStreamReader<T>
    {
        public T Current { get; private set; } = default!;

        public async Task<bool> MoveNext(CancellationToken cancellationToken)
        {
            if (await reader.WaitToReadAsync(cancellationToken).ConfigureAwait(false)
                && reader.TryRead(out var item))
            {
                Current = item;
                return true;
            }

            return false;
        }
    }

    /// <summary>假 CallInvoker：让生成的 GreeterClient 完全跑在内存里</summary>
    public sealed class FakeGreeterInvoker(AsyncDuplexStreamingCall<HttpData, HttpData> call) : CallInvoker
    {
        public Metadata? LastHeaders { get; private set; }

        public override AsyncDuplexStreamingCall<TRequest, TResponse> AsyncDuplexStreamingCall<TRequest, TResponse>(
            Method<TRequest, TResponse> method, string? host, CallOptions options)
        {
            LastHeaders = options.Headers;
            return (AsyncDuplexStreamingCall<TRequest, TResponse>)(object)call;
        }

        public override AsyncUnaryCall<TResponse> AsyncUnaryCall<TRequest, TResponse>(
            Method<TRequest, TResponse> method, string? host, CallOptions options, TRequest request) => throw new NotSupportedException();

        public override AsyncClientStreamingCall<TRequest, TResponse> AsyncClientStreamingCall<TRequest, TResponse>(
            Method<TRequest, TResponse> method, string? host, CallOptions options) => throw new NotSupportedException();

        public override AsyncServerStreamingCall<TResponse> AsyncServerStreamingCall<TRequest, TResponse>(
            Method<TRequest, TResponse> method, string? host, CallOptions options, TRequest request) => throw new NotSupportedException();

        public override TResponse BlockingUnaryCall<TRequest, TResponse>(
            Method<TRequest, TResponse> method, string? host, CallOptions options, TRequest request) => throw new NotSupportedException();
    }

    /// <summary>一条内存里的"客户端隧道"：请求流可读、响应流可写、能查到发出去的头部</summary>
    public sealed class FakeTunnel
    {
        public FakeTunnel()
        {
            var responseStream = new ChannelServerStreamReader<HttpData>(ServerMessages.Reader);
            var call = new AsyncDuplexStreamingCall<HttpData, HttpData>(
                RequestStream,
                responseStream,
                Task.FromResult(new Metadata()),
                static () => Status.DefaultSuccess,
                static () => new Metadata(),
                static () => { });

            Invoker = new FakeGreeterInvoker(call);
            Client = new Greeter.GreeterClient(Invoker);
        }

        /// <summary>客户端写往服务器的消息</summary>
        public RecordingClientStreamWriter<HttpData> RequestStream { get; } = new();

        public Channel<HttpData> ServerMessages { get; } = Channel.CreateUnbounded<HttpData>();

        public FakeGreeterInvoker Invoker { get; }

        public Greeter.GreeterClient Client { get; }

        /// <summary>本次调用实际带上的头部</summary>
        public Metadata? Headers => Invoker.LastHeaders;

        public string? RequestedWith => Headers?.FirstOrDefault(x => x.Key == "x-requested-with")?.Value;

        public void SendData(byte[] data) => ServerMessages.Writer.TryWrite(new HttpData { Payload = ByteString.CopyFrom(data) });

        public void SendHandshake() => ServerMessages.Writer.TryWrite(new HttpData { Payload = ByteString.Empty });

        public void CompleteServerStream() => ServerMessages.Writer.TryComplete();
    }

    /// <summary>假的 GrpcClientFactory：按名字返回内存隧道，并记录被请求的名字</summary>
    public sealed class FakeGrpcClientFactory(Func<string, Greeter.GreeterClient> factory) : GrpcClientFactory
    {
        public List<string> RequestedNames { get; } = [];

        public override TClient CreateClient<TClient>(string name) where TClient : class
        {
            RequestedNames.Add(name);
            return (TClient)(object)factory(name);
        }
    }
}
