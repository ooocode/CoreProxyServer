using CoreProxy.Server.Orleans.Internal;
using DotNext;
using DotNext.IO.Pipelines;
using Microsoft.AspNetCore.Connections;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Net.Http.Headers;
using System.Collections.Concurrent;
using System.Net;
using System.Net.Sockets;
using System.Text;

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
                        ClientIpAddress = $"{iPAddress}:{port}",
                        DateTime = DateTimeOffset.UtcNow
                    });

                    httpContext.Response.ContentType = "application/octet-stream";
                    await httpContext.Response.StartAsync(cancellationToken);

                    var bytes = Encoding.UTF8.GetBytes(connectionId);
                    await httpContext.Response.BodyWriter.WriteAsync(bytes, cancellationToken);
                    await httpContext.Response.BodyWriter.FlushAsync(cancellationToken);

                    await foreach (var item in serverConnectionContext.Transport.Input.ReadAllAsync(cancellationToken))
                    {
                        await httpContext.Response.BodyWriter.WriteAsync(item, cancellationToken);
                        await httpContext.Response.BodyWriter.FlushAsync(cancellationToken);
                    }
                }
                catch (OperationCanceledException)
                {
                    logger.LogWarning("{host}:{port} 取消连接", host, port);
                }
                catch (SocketException ex)
                {
                    logger.LogError(ex, "{host}:{port} 连接服务器失败", host, port);
                }
                catch (Exception ex)
                {
                    logger.LogError(ex, "{host}:{port} 未知错误", host, port);
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
