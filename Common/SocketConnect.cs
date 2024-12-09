using Microsoft.AspNetCore.Connections;
using ServerWebApplication.Services;
using System.IO.Pipelines;
using System.Net;

namespace ServerWebApplication.Common
{
    public partial class SocketConnect(IConnectionFactory connectionFactory, ILogger logger,
        DnsParseService dnsParseService) : IAsyncDisposable
    {
        /// <summary>
        /// Microsoft.AspNetCore.Server.Kestrel.Transport.Sockets/Internal/SocketConnection.cs
        /// </summary>
        private ConnectionContext? connectionContext = null;

        public PipeReader? PipeReader => connectionContext?.Transport.Input;
        public PipeWriter? PipeWriter => connectionContext?.Transport.Output;

        [LoggerMessage(Level = LogLevel.Error, Message = "连接失败：{host}:{port} {errorMessage}")]
        private static partial void LogConnectFail(ILogger logger, string host, int port, string errorMessage);

        public async Task ConnectAsync(string host, int port, CancellationToken cancellationToken)
        {
            try
            {
                if (!IPAddress.TryParse(host, out var iPAddress))
                {
                    iPAddress = await dnsParseService.GetIpAsync(host, port, cancellationToken);
                }

                var endpoint = new IPEndPoint(iPAddress, port);
                connectionContext = await connectionFactory.ConnectAsync(endpoint, cancellationToken);
            }
            catch (Exception ex)
            {
                LogConnectFail(logger, host, port, ex.Message);
                throw;
            }
        }

        public async ValueTask DisposeAsync()
        {
            if (connectionContext != null)
            {
                await connectionContext.DisposeAsync();
            }

            GC.SuppressFinalize(this);
        }
    }
}
