using Grpc.Core;
using Microsoft.AspNetCore.Connections;
using System.IO.Pipelines;
using System.Net;
using System.Net.Sockets;

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
