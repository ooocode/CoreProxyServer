using DotNext.IO.Pipelines;
using Grpc.Core;
using Microsoft.AspNetCore.Connections;
using System.Buffers;
using System.IO.Pipelines;
using System.Net;
using System.Net.Sockets;
using System.Reflection.PortableExecutable;
using System.Runtime.CompilerServices;

namespace ServerWebApplication.Common
{
    public class SocketConnect(IConnectionFactory connectionFactory,
        DnsParseService dnsParseService,
        ILogger logger) : IAsyncDisposable
    {
        private ConnectionContext? connectionContext = null;

        public PipeReader? PipeReader => connectionContext?.Transport.Input;
        public PipeWriter? PipeWriter => connectionContext?.Transport.Output;

        public async Task ConnectAsync(string host, int port, CancellationToken cancellationToken)
        {
            try
            {
                if (!IPAddress.TryParse(host, out var iPAddress))
                {
                    iPAddress = await dnsParseService.GetIpAsync(host, port, cancellationToken: cancellationToken);
                }

                var ipEndPoint = new IPEndPoint(iPAddress, port);
                connectionContext = await connectionFactory.ConnectAsync(ipEndPoint, cancellationToken);
            }
            catch (Exception ex) when (ex is SocketException || ex is OperationCanceledException)
            {
                var err = $"连接失败：{host}:{port} {ex.Message}";
                logger.LogError(err);
                throw new RpcException(new Status(StatusCode.Cancelled, err));
            }
        }

        public async IAsyncEnumerable<ReadOnlyMemory<byte>> ReadAllAsync([EnumeratorCancellation]CancellationToken cancellationToken)
        {
            ArgumentNullException.ThrowIfNull(connectionContext, nameof(connectionContext));

            while (!cancellationToken.IsCancellationRequested)
            {
                var result = await connectionContext.Transport.Input.ReadAsync(cancellationToken).ConfigureAwait(false);
                var buffer = result.Buffer;
                if (buffer.IsEmpty)
                {
                    break;
                }

                try
                {
                    if (buffer.IsSingleSegment)
                    {
                        yield return buffer.First;
                    }
                    else
                    {
                        yield return buffer.ToArray();
                    }
                }
                finally
                {
                    connectionContext.Transport.Input.AdvanceTo(buffer.End);
                }

                if (result.IsCompleted)
                {
                    break;
                }
            }
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
