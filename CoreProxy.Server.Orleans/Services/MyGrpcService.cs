using CoreProxy.Server.Orleans.Internal;
using CoreProxy.Server.Orleans.Models;
using DotNext.IO.Pipelines;
using Google.Protobuf;
using Google.Protobuf.WellKnownTypes;
using Grpc.Core;
using Hello;
using Microsoft.AspNetCore.Connections;
using Microsoft.AspNetCore.Server.Kestrel.Transport.Sockets;
using Microsoft.Net.Http.Headers;
using ServerWebApplication.Common;
using System.Net;
using System.Net.Sockets;

namespace CoreProxy.Server.Orleans.Services
{
    public class MyGrpcService(
        ILogger<MyGrpcService> logger,
        IHostApplicationLifetime hostApplicationLifetime,
        SocketConnectionContextFactory connectionFactory,
        CertificatePassword certificatePassword) : Greeter.GreeterBase
    {
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

            string sessionId = Guid.CreateVersion7().ToString("N");

            using var timeoutCancellationTokenSource = new CancellationTokenSource(TimeSpan.FromHours(1));
            using var cancellationSource = CancellationTokenSource.CreateLinkedTokenSource(
                        context.CancellationToken, hostApplicationLifetime.ApplicationStopping, timeoutCancellationTokenSource.Token);
            var cancellationToken = cancellationSource.Token;

            var iPAddresses = await DnsService.GetIpAddressesAsync(request.Host, cancellationToken);
            if (iPAddresses == null || iPAddresses.Length == 0)
            {
                throw new RpcException(new Status(StatusCode.InvalidArgument, $"Host[{request.Host}] not found"));
            }

            try
            {
                using var socket = new Socket(SocketType.Stream, ProtocolType.Tcp)
                {
                    NoDelay = true
                };

                await socket.ConnectAsync(iPAddresses, request.Port, CancellationToken.None);
                await using var connectionContext = connectionFactory.Create(socket);

                var connectItem = new ConnectItem
                {
                    ClientIpAddress = context.GetHttpContext().Connection.RemoteIpAddress?.ToString() ?? string.Empty,
                    ConnectionContext = connectionContext,
                    DateTime = DateTimeOffset.Now
                };

                if (!GlobalState.Sockets.TryAdd(sessionId, connectItem))
                {
                    throw new RpcException(new Status(StatusCode.AlreadyExists, "SessionId already exists"));
                }

                //发送包，表示连接成功
                await responseStream.WriteAsync(new HttpData
                {
                    Payload = ByteString.CopyFromUtf8(sessionId),
                }, cancellationToken);

                //读取目标服务器数据
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
                GlobalState.Sockets.TryRemove(sessionId, out _);
            }
        }

        public override async Task<Empty> Send(SendRequest request, ServerCallContext context)
        {
            if (!GlobalState.Sockets.TryGetValue(request.SessionId, out var connectionContext))
            {
                throw new RpcException(new Status(StatusCode.NotFound, "SessionId not found"));
            }

            ArgumentNullException.ThrowIfNull(connectionContext.ConnectionContext, "ConnectionContext is not initialized. Please call Connect method first.");

            await connectionContext.ConnectionContext.Transport.Output.WriteAsync(request.Payload.Memory, context.CancellationToken);
            return new Empty();
        }

        public override Task<StatusReply> GetStatus(GetStatusRequest request, ServerCallContext context)
        {
            CheckPassword(context);

            var reply = new StatusReply
            {
                SocketCount = GlobalState.Sockets.Count
            };

            if (request.IncludeDetail)
            {
                reply.Connections.AddRange(GlobalState.Sockets.Values
                    .Select(x => new StatusDetail
                    {
                        IpAddress = x.ClientIpAddress,
                        DateTime = Timestamp.FromDateTimeOffset(x.DateTime)
                    }));
            }

            return Task.FromResult(reply);
        }
    }
}