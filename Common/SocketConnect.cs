using DnsClient;
using Microsoft.AspNetCore.Connections;
using System;
using System.IO.Pipelines;
using System.Linq;
using System.Net;
using System.Threading;
using System.Threading.Tasks;

namespace ServerWebApplication.Common
{
    public class SocketConnect(IConnectionFactory connectionFactory, ILookupClient lookupClient) : IAsyncDisposable
    {
        private ConnectionContext? connectionContext = null;

        public PipeReader? PipeReader => connectionContext?.Transport.Input;

        public async ValueTask ConnectAsync(string host, int port, CancellationToken cancellationToken)
        {
            if (!IPAddress.TryParse(host, out var iPAddress))
            {
                var result = await lookupClient.QueryAsync(host, QueryType.A, cancellationToken: cancellationToken);
                iPAddress = result.Answers.ARecords().FirstOrDefault()?.Address;
            }

            if (iPAddress == null)
            {
                throw new ArgumentException(host + ":" + host);
            }

            var ipEndPoint = new IPEndPoint(iPAddress, port);
            connectionContext = await connectionFactory.ConnectAsync(ipEndPoint, cancellationToken);
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

                await connectionContext.DisposeAsync();
            }
        }
    }
}
