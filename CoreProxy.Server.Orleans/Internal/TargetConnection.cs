using System.Net;
using System.Net.Sockets;

namespace CoreProxy.Server.Orleans.Internal
{
    /// <summary>
    /// 连往目标服务器的连接。
    /// 不用 Kestrel 的 IConnectionFactory：它经过内部 SocketSender 队列异步发送，
    /// Transport.Output 的 FlushAsync 返回时数据可能还没进内核，
    /// 半关闭补发的 FIN 会抢在数据前面把流截断（实测 423905 字节丢 565 字节，且只在负载下偶发）。
    /// 自己持有 Socket 时，await SendAsync 返回即数据已交给内核，FIN 的顺序才有硬保证。
    /// </summary>
    public sealed class TargetConnection : IAsyncDisposable
    {
        private readonly Socket socket;

        private TargetConnection(Socket socket) => this.socket = socket;

        public static async Task<TargetConnection> ConnectAsync(IPEndPoint endPoint, CancellationToken cancellationToken)
        {
            var socket = new Socket(endPoint.AddressFamily, SocketType.Stream, ProtocolType.Tcp)
            {
                //转发的是交互式流量，禁用 Nagle，避免小包被延迟合并
                NoDelay = true
            };

            try
            {
                await socket.ConnectAsync(endPoint, cancellationToken).ConfigureAwait(false);
                return new TargetConnection(socket);
            }
            catch
            {
                socket.Dispose();
                throw;
            }
        }

        /// <summary>
        /// 写入：循环直到全部字节都交给内核（SendAsync 允许只发出去一部分）。
        /// 返回之后补发 FIN 才是安全的，这是半关闭不截断数据的前提。
        /// </summary>
        public async ValueTask WriteAsync(ReadOnlyMemory<byte> data, CancellationToken cancellationToken)
        {
            while (!data.IsEmpty)
            {
                var sent = await socket.SendAsync(data, SocketFlags.None, cancellationToken).ConfigureAwait(false);
                if (sent <= 0)
                {
                    throw new IOException("写入目标服务器的数据不完整");
                }

                data = data[sent..];
            }
        }

        public ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken) =>
            socket.ReceiveAsync(buffer, SocketFlags.None, cancellationToken);

        /// <summary>
        /// 半关闭：只关闭发送方向（FIN），接收方向继续可用。
        /// 调用前必须确保所有数据都已 WriteAsync 完成，否则 FIN 会排到未发数据前面
        /// </summary>
        public void ShutdownSend() => socket.Shutdown(SocketShutdown.Send);

        public ValueTask DisposeAsync()
        {
            socket.Dispose();
            return ValueTask.CompletedTask;
        }
    }
}
