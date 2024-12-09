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

        [LoggerMessage(Level = LogLevel.Information, Message = "成功解析IPV6：{hostName} -> {ipAddress}")]
        private static partial void LogDnsParseInfoV6(ILogger logger, string hostName, string ipAddress);

        [LoggerMessage(Level = LogLevel.Information, Message = "成功解析IPV4：{hostName} -> {ipAddress}")]
        private static partial void LogDnsParseInfoV4(ILogger logger, string hostName, string ipAddress);

        public async Task ConnectAsync(string host, int port, CancellationToken cancellationToken)
        {
            try
            {
                if (!IPAddress.TryParse(host, out var iPAddress))
                {
                    iPAddress = await dnsParseService.GetIpAsync(host, port, cancellationToken);
                    if (iPAddress.AddressFamily == System.Net.Sockets.AddressFamily.InterNetwork)
                    {
                        LogDnsParseInfoV4(logger, host, iPAddress.ToString());
                    }
                    else if (iPAddress.AddressFamily == System.Net.Sockets.AddressFamily.InterNetworkV6)
                    {
                        LogDnsParseInfoV6(logger, host, iPAddress.ToString());
                    }
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
