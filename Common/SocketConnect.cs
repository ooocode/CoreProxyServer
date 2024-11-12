using Microsoft.AspNetCore.Connections;
using System.IO.Pipelines;
using System.Net;

namespace ServerWebApplication.Common
{
    public class SocketConnect(IConnectionFactory connectionFactory, DnsParseService dnsParseService) : IAsyncDisposable
    {
        private ConnectionContext? connectionContext = null;

        public PipeReader? PipeReader => connectionContext?.Transport.Input;
        public PipeWriter? PipeWriter => connectionContext?.Transport.Output;

        public async Task ConnectAsync(string host, int port, CancellationToken cancellationToken)
        {
            if (!IPAddress.TryParse(host, out var iPAddress))
            {
                iPAddress = await dnsParseService.GetIpAsync(host, port, cancellationToken: cancellationToken);
            }

            var ipEndPoint = new IPEndPoint(iPAddress, port);
            connectionContext = await connectionFactory.ConnectAsync(ipEndPoint, cancellationToken);
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
