using Grpc.Core;
using Microsoft.AspNetCore.Connections;
using System.IO.Pipelines;
using System.Net;
using System.Net.Sockets;

namespace ServerWebApplication.Common
{
    public class SocketConnect(IConnectionFactory connectionFactory, ILogger logger) : IAsyncDisposable
    {
        private ConnectionContext? connectionContext = null;

        public PipeReader? PipeReader => connectionContext?.Transport.Input;
        public PipeWriter? PipeWriter => connectionContext?.Transport.Output;

        public async Task ConnectAsync(string host, int port, CancellationToken cancellationToken)
        {
            try
            {
                System.Net.EndPoint? endpoint = null;
                if (IPAddress.TryParse(host, out var iPAddress))
                {
                    endpoint = new IPEndPoint(iPAddress, port);
                }
                else
                {
                    endpoint = new DnsEndPoint(host, port);
                }

                connectionContext = await connectionFactory.ConnectAsync(endpoint, cancellationToken);
            }
            catch (Exception ex) when (ex is SocketException || ex is OperationCanceledException)
            {
                var err = $"连接失败：{host}:{port} {ex.Message}";
                logger.LogError(err);
                throw new RpcException(new Status(StatusCode.Cancelled, err));
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
