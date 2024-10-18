using Microsoft.AspNetCore.Connections;
using System;
using System.Buffers;
using System.Collections.Generic;
using System.IO.Pipelines;
using System.Linq;
using System.Net;
using System.Runtime.CompilerServices;
using System.Threading;
using System.Threading.Tasks;

namespace ServerWebApplication.Common
{
    public class SocketConnect(IConnectionFactory connectionFactory) : IAsyncDisposable
    {
        private ConnectionContext? connectionContext = null;

        public async ValueTask ConnectAsync(string host, int port, CancellationToken cancellationToken)
        {
            var iPAddresses = await Dns.GetHostAddressesAsync(host, cancellationToken);
            if (iPAddresses == null || iPAddresses.Length == 0)
            {
                throw new ArgumentException(host + ":" + host);
            }

            var ipEndPoint = new IPEndPoint(iPAddresses[0], port);
            connectionContext = await connectionFactory.ConnectAsync(ipEndPoint, cancellationToken);
        }

        public async IAsyncEnumerable<ReadOnlyMemory<byte>> LoopRecvDataAsync(
            [EnumeratorCancellation] CancellationToken cancellationToken)
        {
            ArgumentNullException.ThrowIfNull(connectionContext, nameof(connectionContext));
            while (!cancellationToken.IsCancellationRequested)
            {
                //浏览器普通接收
                var result = await connectionContext.Transport.Input.ReadAsync(cancellationToken);
                ReadOnlySequence<byte> buffer = result.Buffer;

                if (buffer.IsEmpty)
                {
                    break;
                }

                if (buffer.IsSingleSegment)
                {
                    yield return buffer.First;
                }
                else
                {
                    yield return buffer.ToArray();
                }

                connectionContext.Transport.Input.AdvanceTo(buffer.End);

                // Stop reading if there's no more data coming.
                if (result.IsCompleted)
                {
                    break;
                }
            }
            // 完成读取
            await connectionContext.Transport.Input.CompleteAsync();
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
        }
    }
}
