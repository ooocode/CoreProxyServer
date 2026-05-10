using CoreProxy.Server.Orleans.Internal;
using DotNext;
using DotNext.IO.Pipelines;
using Microsoft.AspNetCore.Connections;
using Microsoft.AspNetCore.Mvc;
using System.Buffers.Text;
using System.Collections.Concurrent;
using System.Net;
using System.Runtime.CompilerServices;
using System.Text;

namespace CoreProxy.Server.Orleans.Services
{
    public static class HttpMap
    {
        private static readonly ConcurrentDictionary<string, ConnectionContext> servers = [];

        public static void MapProxy(WebApplication app)
        {
            app.MapPost("/push", async (string connectionId,
              HttpContext httpContext,
              CancellationToken cx,
              [FromServices] IHostApplicationLifetime hostApplicationLifetime) =>
            {
                if (!servers.TryGetValue(connectionId, out var connectionContext))
                {
                    return Results.NotFound();
                }

                using var cancellationSource = CancellationTokenSource.CreateLinkedTokenSource(
                          cx, hostApplicationLifetime.ApplicationStopping);
                var cancellationToken = cancellationSource.Token;

                await foreach (var item in httpContext.Request.BodyReader.ReadAllAsync(cancellationToken))
                {
                    await connectionContext.Transport.Output.WriteAsync(item, cancellationToken);
                }
                return Results.Ok();
            });

            app.MapGet("/sse", async (
                [FromQuery] string host,
                [FromQuery] int port,
                [FromServices] IHostApplicationLifetime hostApplicationLifetime,
                [FromServices] IConnectionFactory connectionFactory,
                CancellationToken cancellationToken) =>
            {
                var ips = await Dns.GetHostAddressesAsync(host, cancellationToken).WaitAsync(TimeSpan.FromMinutes(1), cancellationToken);
                var ip = ips.OrderBy(x => x.AddressFamily).First();

                return TypedResults.ServerSentEvents(ServerHanderAsync(hostApplicationLifetime, connectionFactory, ip, port, cancellationToken));
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
