using Microsoft.AspNetCore.Connections;
using Microsoft.AspNetCore.Server.Kestrel.Transport.Sockets;
using System;
using System.Buffers;
using System.Collections.Generic;
using System.IO.Pipelines;
using System.Net;
using System.Net.Sockets;
using System.Runtime.CompilerServices;
using System.Threading;
using System.Threading.Tasks;

namespace ServerWebApplication
{
    public class SocketConnect : IAsyncDisposable
    {
        private readonly SocketConnectionContextFactory connectionFactory;

        public SocketConnect(SocketConnectionContextFactory connectionFactory)
        {
            this.connectionFactory = connectionFactory;
        }

        public ConnectionContext? connectionContext = null;

        public async Task ConnectAsync(IPAddress host, int port, CancellationToken cancellationToken)
        {
            var iPEndPoint = new IPEndPoint(host, port);

            var socket = new Socket(AddressFamily.InterNetwork, SocketType.Stream, ProtocolType.Tcp);
            await socket.ConnectAsync(iPEndPoint, cancellationToken);
            connectionContext = connectionFactory.Create(socket);
        }

        public async ValueTask DisposeAsync()
        {
            if (connectionContext != null)
            {
                await connectionContext.Transport.Input.CompleteAsync();
                await connectionContext.Transport.Output.CompleteAsync();
            }
        }

        public async IAsyncEnumerable<ReadOnlyMemory<byte>> LoopRecvDataAsync(
            [EnumeratorCancellation]
            CancellationToken cancellation = default)
        {
            while (true)
            {
                // 从PipeWriter至少分配512字节
                //Memory<byte> memory = pipe.Writer.GetMemory(minimumBufferSize);

                var result = await connectionContext!.Transport.Input.ReadAsync(cancellation);
                ReadOnlySequence<byte> buffer = result.Buffer;


                foreach (var memory in buffer)
                {
                    yield return memory;
                }

                connectionContext.Transport.Input.AdvanceTo(buffer.End);

                if (result.IsCompleted)
                {
                    break;
                }
            }
        }

        public ValueTask<FlushResult> SendAsync(ReadOnlyMemory<byte> memory, CancellationToken cancellationToken)
        {
            ArgumentNullException.ThrowIfNull(connectionContext);
            return connectionContext.Transport.Output.WriteAsync(memory, cancellationToken);
        }
    }
}
