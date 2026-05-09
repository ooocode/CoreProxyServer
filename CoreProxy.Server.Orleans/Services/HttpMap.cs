using CoreProxy.Server.Orleans.Internal;
using DotNext;
using DotNext.IO.Pipelines;
using Microsoft.AspNetCore.Connections;
using Microsoft.AspNetCore.Mvc;
using System.Buffers.Text;
using System.Net;
using System.Runtime.CompilerServices;
using System.Text;

namespace CoreProxy.Server.Orleans.Services
{
    public static class HttpMap
    {
        private static readonly System.Collections.Concurrent.ConcurrentDictionary<string, ConnectionContext> servers = [];

        public static void MapProxy(WebApplication app)
        {
            app.MapGet("/sse", async (
                [FromQuery] string host,
                [FromQuery] int port,
                [FromServices] IHostApplicationLifetime hostApplicationLifetime,
                [FromServices] IConnectionFactory connectionFactory,
                CancellationToken cx) =>
            {
                var ips = await Dns.GetHostAddressesAsync(host, cx);
                var ip = ips.OrderBy(x => x.AddressFamily).First();

                return TypedResults.ServerSentEvents(ServerHanderAsync(hostApplicationLifetime, connectionFactory, ip, port, cx));
            });

            app.MapGet("/push", async (string connectionId, string data, CancellationToken cx,
                [FromServices] IHostApplicationLifetime hostApplicationLifetime) =>
            {
                if (!servers.TryGetValue(connectionId, out var connectionContext))
                {
                    return Results.NotFound();
                }

                using var cancellationSource = CancellationTokenSource.CreateLinkedTokenSource(
                          cx, hostApplicationLifetime.ApplicationStopping);
                var cancellationToken = cancellationSource.Token;

                var bytes = Base64Url.DecodeFromUtf8(Encoding.UTF8.GetBytes(data));
                await connectionContext.Transport.Output.WriteAsync(bytes, cancellationToken);
                return Results.Ok();
            });
        }

        private static async IAsyncEnumerable<string> ServerHanderAsync(
            IHostApplicationLifetime hostApplicationLifetime,
            IConnectionFactory connectionFactory,
            IPAddress iPAddress, int port,
            [EnumeratorCancellation] CancellationToken ct)
        {
            using var cancellationSource = CancellationTokenSource.CreateLinkedTokenSource(
                           ct, hostApplicationLifetime.ApplicationStopping);
            var cancellationToken = cancellationSource.Token;

            string connectionId = Guid.CreateVersion7().ToString("N");

            try
            {
                await using var serverConnectionContext = await connectionFactory.ConnectAsync(new IPEndPoint(iPAddress, port), cancellationToken);

                servers.TryAdd(connectionId, serverConnectionContext);
                GlobalState.Connections.TryAdd(connectionId, new ConnectItem
                {
                    ClientIpAddress = $"{iPAddress}:{port}",
                    DateTime = DateTimeOffset.UtcNow
                });

                //发送空包，表示连接成功
                yield return $"id:{connectionId}";

                await foreach (var item in serverConnectionContext.Transport.Input.ReadAllAsync(cancellationToken))
                {
                    yield return Convert.ToBase64String(item.Span);
                }
            }
            finally
            {
                GlobalState.Connections.TryRemove(connectionId, out var _);
                servers.TryRemove(connectionId, out var _);
            }
        }
    }
}
