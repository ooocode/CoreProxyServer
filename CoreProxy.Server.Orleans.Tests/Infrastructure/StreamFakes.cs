using Google.Protobuf;
using Grpc.Core;
using Hello;
using System.Threading.Channels;

namespace CoreProxy.Server.Orleans.Tests.Infrastructure
{
    /// <summary>
    /// 由 Channel 驱动，模拟 gRPC 客户端的请求流。
    /// Channel 完成后再调用 MoveNext 返回 false，等价于客户端半关闭（发完请求）。
    /// </summary>
    public sealed class ChannelStreamReader<T>(ChannelReader<T> reader) : IAsyncStreamReader<T>
    {
        public T Current { get; private set; } = default!;

        /// <summary>诊断用：记录每次 MoveNext 消费到的消息</summary>
        public List<T> Consumed { get; } = [];

        public async Task<bool> MoveNext(CancellationToken cancellationToken)
        {
            if (await reader.WaitToReadAsync(cancellationToken).ConfigureAwait(false)
                && reader.TryRead(out var item))
            {
                Current = item;
                Consumed.Add(item);
                return true;
            }

            return false;
        }
    }

    /// <summary>
    /// 把服务端写出的消息收集进 Channel，模拟 gRPC 的服务端响应流。
    /// </summary>
    public sealed class HttpDataStreamWriter(ChannelWriter<HttpData> writer, CancellationToken contextToken) : IServerStreamWriter<HttpData>
    {
        public WriteOptions? WriteOptions { get; set; }

        public Task WriteAsync(HttpData message) => WriteAsync(message, CancellationToken.None);

        /// <summary>
        /// 必须提供这个重载。Grpc.Core 给 IAsyncStreamWriter&lt;T&gt; 的默认接口实现会抛
        /// NotSupportedException("Cancellation of stream writes is not supported by this gRPC implementation.")，
        /// 而生产代码一直用的是带 token 的重载。真实实现（Grpc.AspNetCore.Server 的 HttpContextStreamWriter）
        /// 是以显式接口实现的方式提供它的，测试替身这里用隐式实现。
        /// </summary>
        public Task WriteAsync(HttpData message, CancellationToken cancellationToken)
        {
            if (cancellationToken.IsCancellationRequested)
            {
                return Task.FromCanceled(cancellationToken);
            }

            //真实 gRPC 在客户端消失（RPC 取消）之后写入会失败
            if (contextToken.IsCancellationRequested)
            {
                return Task.FromCanceled(contextToken);
            }

            //生产代码用 UnsafeByteOperations.UnsafeWrap 直接包装 PipeReader 的内存，该内存随后会被管道复用。
            //真实 gRPC 会在 WriteAsync 内部立即序列化，所以这里也必须当场拷贝一份，
            //否则测试读到的是被覆盖后的数据，会产生假阳性/假阴性
            var copy = new HttpData
            {
                Payload = ByteString.CopyFrom(message.Payload.Span),
                UnixTimeMilliseconds = message.UnixTimeMilliseconds
            };

            return writer.WriteAsync(copy, CancellationToken.None).AsTask();
        }
    }
}
