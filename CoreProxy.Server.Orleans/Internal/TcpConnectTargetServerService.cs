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
                connectionContext = connectionFactory.Create(socket);
            }
            catch (SocketException ex)
            {
                throw new Exception($"Failed to connect to target server {host}:{port} {ex.Message}", ex);
            }
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

        public async ValueTask DisposeAsync()
        {
            if (connectionContext == null && socket != null)
            {
                socket.Shutdown(SocketShutdown.Both);
                socket?.Dispose();
            }
            else if (connectionContext != null)
            {
                await connectionContext.DisposeAsync();
            }
        }
    }
}