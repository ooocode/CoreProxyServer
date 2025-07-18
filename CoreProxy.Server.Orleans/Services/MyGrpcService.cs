using CoreProxy.Server.Orleans.Internal;
using CoreProxy.Server.Orleans.Models;
using DotNext.IO.Pipelines;
using Google.Protobuf;
using Google.Protobuf.WellKnownTypes;
using Grpc.Core;
using Hello;
using Microsoft.AspNetCore.Connections;
using Microsoft.Net.Http.Headers;
using ServerWebApplication.Common;
using System.Net;
using System.Net.Sockets;

public class MyGrpcService(
    ILogger<MyGrpcService> logger,
    IHostApplicationLifetime hostApplicationLifetime,
    IConnectionFactory connectionFactory,
    CertificatePassword certificatePassword) : Greeter.GreeterBase
{
    private readonly static HttpData EmptyHttpData = new()
    {
        Payload = ByteString.Empty
    };

    private void CheckPassword(ServerCallContext context)
    {
        var ipAddress = context.GetHttpContext().Connection.RemoteIpAddress;
        ArgumentNullException.ThrowIfNull(ipAddress, nameof(ipAddress));
        if (IPAddress.IsLoopback(ipAddress))
        {
            return;
        }

        var password = context.RequestHeaders.GetValue(HeaderNames.Authorization)
           ?.Replace("Password ", string.Empty);
        if (string.IsNullOrWhiteSpace(password) || !string.Equals(password, certificatePassword.Password, StringComparison.Ordinal))
        {
            throw new RpcException(new Status(StatusCode.Unauthenticated, string.Empty));
        }
    }

    public override async Task Connect(ConnectRequest request, IServerStreamWriter<HttpData> responseStream, ServerCallContext context)
    {
        CheckPassword(context);

        if (GlobalState.Sockets.ContainsKey(request.SessionId))
        {
            throw new RpcException(new Status(StatusCode.AlreadyExists, "SessionId already exists"));
        }

        using var cancellationSource = CancellationTokenSource.CreateLinkedTokenSource(
                    context.CancellationToken, hostApplicationLifetime.ApplicationStopping);
        var cancellationToken = cancellationSource.Token;

        try
        {
            var endpoint = await DnsService.GetIpEndpointAsync(request.Host, request.Port, cancellationToken);
            await using var connectionContext = await connectionFactory.ConnectAsync(endpoint, cancellationToken);
            if (!GlobalState.Sockets.TryAdd(request.SessionId, connectionContext))
            {
                throw new RpcException(new Status(StatusCode.AlreadyExists, "SessionId already exists"));
            }

            //发送空包，表示连接成功
            await responseStream.WriteAsync(EmptyHttpData, cancellationToken);

            await foreach (var item in connectionContext.Transport.Input.ReadAllAsync(cancellationToken))
            {
                HttpData httpData = new()
                {
                    Payload = UnsafeByteOperations.UnsafeWrap(item)
                };
                await responseStream.WriteAsync(httpData, cancellationToken);
            }
        }
        catch (ConnectionResetException)
        {
            // ignored
        }
        catch (SocketException)
        {
            // ignored
        }
        catch (OperationCanceledException)
        {
            // ignored
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Connect error");
        }
        finally
        {
            GlobalState.Sockets.TryRemove(request.SessionId, out _);
        }
    }

    public override async Task<Empty> Send(SendRequest request, ServerCallContext context)
    {
        CheckPassword(context);

        if (!GlobalState.Sockets.TryGetValue(request.SessionId, out var connectionContext))
        {
            throw new RpcException(new Status(StatusCode.NotFound, "SessionId not found"));
        }

        await connectionContext.Transport.Output.WriteAsync(request.Payload.Memory, context.CancellationToken);
        return new Empty();
    }

    public override Task<StatusReply> GetStatus(Empty request, ServerCallContext context)
    {
        StatusReply reply = new()
        {
            SocketCount = GlobalState.Sockets.Count
        };
        return Task.FromResult(reply);
    }
}