﻿using Microsoft.AspNetCore.Connections;
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

namespace ServerWebApplication.Common
{
    public class SocketConnect : IAsyncDisposable
    {
        private readonly SocketConnectionContextFactory connectionFactory;

        public SocketConnect(SocketConnectionContextFactory connectionFactory)
        {
            this.connectionFactory = connectionFactory;
        }

        private Socket? socket = null;
        public ConnectionContext? connectionContext = null;

        public async Task ConnectAsync(string host, int port, CancellationToken cancellationToken)
        {
            socket = new Socket(AddressFamily.InterNetwork, SocketType.Stream, ProtocolType.Tcp);
            await socket.ConnectAsync(host, port, cancellationToken);
            connectionContext = connectionFactory.Create(socket);
        }

        public async IAsyncEnumerable<ReadOnlyMemory<byte>> LoopRecvDataAsync(
            [EnumeratorCancellation] CancellationToken cancellationToken)
        {
            while (!cancellationToken.IsCancellationRequested)
            {
                //浏览器普通接收
                var result = await connectionContext!.Transport.Input.ReadAsync(cancellationToken);
                ReadOnlySequence<byte> buffer = result.Buffer;

                foreach (var memory in buffer)
                {
                    yield return memory;
                }

                connectionContext.Transport.Input.AdvanceTo(buffer.End);

                // Stop reading if there's no more data coming.
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


        public async ValueTask DisposeAsync()
        {
            if (connectionContext != null)
            {
                await connectionContext.Transport.Input.CompleteAsync();
                await connectionContext.Transport.Output.CompleteAsync();
            }

            if (socket != null)
            {
                socket.Shutdown(SocketShutdown.Both);
                socket.Close();
                socket.Dispose();
            }
        }
    }
}
