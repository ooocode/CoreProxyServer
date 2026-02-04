using DotNext.IO.Pipelines;
using Microsoft.AspNetCore.Connections;
using Microsoft.AspNetCore.Server.Kestrel.Transport.Sockets;
using System.Net.Sockets;

namespace CoreProxy.Server.Orleans.Internal
{
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
            try
            {
                await socket.ConnectAsync(host, port, cancellationToken);
            }
            catch
            {
                socket.Dispose();
                socket = null;
                throw;
            }
            connectionContext = connectionFactory.Create(socket);
        }

        public async Task SendAsync(ReadOnlyMemory<byte> data, CancellationToken cancellationToken)
        {
            ArgumentNullException.ThrowIfNull(connectionContext, "ConnectionContext is not initialized. Please call Connect method first.");
            await connectionContext.Transport.Output.WriteAsync(data, cancellationToken);
        }

        public IAsyncEnumerable<ReadOnlyMemory<byte>> ReceiveAsync(CancellationToken cancellationToken)
        {
            ArgumentNullException.ThrowIfNull(connectionContext, "ConnectionContext is not initialized. Please call Connect method first.");
            return connectionContext.Transport.Input.ReadAllAsync(cancellationToken);
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