using DotNext.IO.Pipelines;
using Google.Protobuf;
using Hello;
using Microsoft.AspNetCore.Connections;
using Microsoft.AspNetCore.Server.Kestrel.Transport.Sockets;
using System.IO.Pipelines;
using System.Net.Sockets;
using System.Runtime.CompilerServices;

namespace CoreProxy.Server.Orleans.Internal
{
    public enum ServerStatus
    {
        Data = 1,
        Finish = 2
    }

    public class TcpConnectTargetServerService(
        SocketConnectionContextFactory connectionFactory,
        string host, int port) : IConnectTargetServerService
    {
        private Socket? socket;
        private ConnectionContext? connectionContext;

        public async Task ConnectAsync(CancellationToken cancellationToken)
        {
            socket = new Socket(SocketType.Stream, ProtocolType.Tcp)
            {
                NoDelay = true
            };

            //60秒连接超时
            using var cancellationSource = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            cancellationSource.CancelAfter(TimeSpan.FromSeconds(60));

            try
            {
                await socket.ConnectAsync(host, port, cancellationSource.Token);
            }
            catch
            {
                socket.Dispose();
                socket = null;
                throw;
            }
            connectionContext = connectionFactory.Create(socket);
        }

        public ValueTask<FlushResult> SendAsync(ReadOnlyMemory<byte> data, CancellationToken cancellationToken)
        {
            ArgumentNullException.ThrowIfNull(connectionContext, "ConnectionContext is not initialized. Please call Connect method first.");
            return connectionContext.Transport.Output.WriteAsync(data, cancellationToken);
        }

        public IAsyncEnumerable<ReadOnlyMemory<byte>> ReceiveAsync(CancellationToken cancellationToken)
        {
            ArgumentNullException.ThrowIfNull(connectionContext, "ConnectionContext is not initialized. Please call Connect method first.");
            return connectionContext.Transport.Input.ReadAllAsync(cancellationToken);
        }

        public async IAsyncEnumerable<HttpData> ReceiveAsHttpDataAsync([EnumeratorCancellation] CancellationToken cancellationToken)
        {
            ArgumentNullException.ThrowIfNull(connectionContext, "ConnectionContext is not initialized. Please call Connect method first.");
            await foreach (var item in connectionContext.Transport.Input.ReadAllAsync(cancellationToken))
            {
                yield return new HttpData
                {
                    Payload = UnsafeByteOperations.UnsafeWrap(item),
                    UnixTimeMilliseconds = ServerStatus.Data.GetHashCode()
                };
            }
        }

#pragma warning disable CA1816
        public async ValueTask DisposeAsync()
        {
            if (connectionContext != null)
            {
                await connectionContext.DisposeAsync();
            }
        }
#pragma warning restore CA1816
    }
}