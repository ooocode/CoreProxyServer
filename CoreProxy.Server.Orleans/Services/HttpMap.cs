using CoreProxy.Server.Orleans.Internal;
using DotNext;
using DotNext.IO.Pipelines;
using Microsoft.AspNetCore.Connections;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Net.Http.Headers;
using System.Collections.Concurrent;
using System.IO;
using System.IO.Pipelines;
using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Threading.Channels;

namespace CoreProxy.Server.Orleans.Services
{
    public static class HttpMap
    {
        private static readonly ConcurrentDictionary<string, ConnectionContext> servers = [];

        public static void MapProxy(WebApplication app)
        {
            app.MapPost("/push/{connectionId}", async (string connectionId,
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

            app.MapGet("/stream", async (
             HttpContext httpContext,
             [FromQuery] string host,
             [FromQuery] int port,
             [FromServices] IHostApplicationLifetime hostApplicationLifetime,
             [FromServices] IConnectionFactory connectionFactory,
             [FromServices] ILogger<Program> logger,
             CancellationToken ct) =>
            {
                using var cancellationSource = CancellationTokenSource.CreateLinkedTokenSource(
                          ct, hostApplicationLifetime.ApplicationStopping);
                var cancellationToken = cancellationSource.Token;

                string connectionId = Guid.CreateVersion7().ToString("N");
                try
                {
                    var ips = await Dns.GetHostAddressesAsync(host, cancellationToken).WaitAsync(TimeSpan.FromMinutes(1), cancellationToken);
                    var iPAddress = ips.OrderBy(x => x.AddressFamily).First();

                    await using var serverConnectionContext = await connectionFactory.ConnectAsync(new IPEndPoint(iPAddress, port), cancellationToken);

                    servers.TryAdd(connectionId, serverConnectionContext);
                    GlobalState.Connections.TryAdd(connectionId, new ConnectItem
                    {
                        ClientIpAddress = $"{host}:{port}",
                        DateTime = DateTimeOffset.UtcNow
                    });

                    httpContext.Response.ContentType = "application/octet-stream";
                    await httpContext.Response.StartAsync(cancellationToken);

                    //发送连接Id
                    var writer = httpContext.Response.BodyWriter;
                    var bytes = Encoding.UTF8.GetBytes(connectionId);
                    await writer.WriteAsync(bytes, cancellationToken);
                    await writer.FlushAsync(cancellationToken);

                    //转发内容
                    await serverConnectionContext.Transport.Input.CopyToAsync(writer, cancellationToken);
                }
                catch (SocketException ex)
                {
                    logger.LogError(ex, "SocketException");
                }
                catch (ConnectionResetException ex)
                {
                    logger.LogError(ex, "ConnectionResetException");
                }
                catch (OperationCanceledException)
                {
                    if (logger.IsEnabled(LogLevel.Trace))
                    {
                        logger.LogTrace("OperationCanceledException");
                    }
                }
                finally
                {
                    GlobalState.Connections.TryRemove(connectionId, out var _);
                    servers.TryRemove(connectionId, out var _);
                }
            });
        }
    }
}
