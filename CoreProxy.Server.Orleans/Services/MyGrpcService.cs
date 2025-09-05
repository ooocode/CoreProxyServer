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
using System.Net;
using System.Net.Sockets;

namespace CoreProxy.Server.Orleans.Services
{
    public class MyGrpcService(
        IHostApplicationLifetime hostApplicationLifetime,
        SocketConnectionContextFactory connectionFactory,
        CertificatePassword certificatePassword) : Greeter.GreeterBase
    {
        private void CheckPassword(ServerCallContext context)
        {
            var ipAddress = context.GetHttpContext().Connection.RemoteIpAddress;
            if (ipAddress == null || IPAddress.IsLoopback(ipAddress))
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

        public override async Task StreamHandler(IAsyncStreamReader<HttpData> requestStream, IServerStreamWriter<HttpData> responseStream, ServerCallContext context)
        {
            CheckPassword(context);
            var hostAndPort = context.RequestHeaders.GetValue(HeaderNames.XRequestedWith);
            if (string.IsNullOrWhiteSpace(hostAndPort))
            {
                throw new RpcException(new Status(StatusCode.InvalidArgument, string.Empty));
            }

            Uri uri = new(hostAndPort);
            var host = uri.Host;
            var port = uri.Port;

            using var timeoutCancellationTokenSource = new CancellationTokenSource(TimeSpan.FromHours(1));
            using var cancellationSource = CancellationTokenSource.CreateLinkedTokenSource(
                        context.CancellationToken, hostApplicationLifetime.ApplicationStopping, timeoutCancellationTokenSource.Token);
            var cancellationToken = cancellationSource.Token;

            string connectionId = Guid.CreateVersion7().ToString("N");

            try
            {
                //添加连接信息
                GlobalState.Sockets.TryAdd(connectionId, new ConnectItem
                {
                    ClientIpAddress = context.Peer,
                    DateTime = DateTimeOffset.UtcNow
                });

                using var socket = new Socket(SocketType.Stream, ProtocolType.Tcp)
                {
                    NoDelay = true
                };

                await socket.ConnectAsync(host, port, cancellationToken);
                await using ConnectionContext connectionContext = connectionFactory.Create(socket);

                //发送空包，表示连接成功
                await responseStream.WriteAsync(new HttpData
                {
                    Payload = ByteString.Empty,
                }, cancellationToken);


                var taskClient = HandlerClientAsync(connectionContext, requestStream, cancellationToken);
                var taskServer = HandlerServerAsync(connectionContext, responseStream, cancellationToken);

                await foreach (var task in Task.WhenEach(taskClient, taskServer))
                {
                    if (task.Id == taskClient.Id)
                    {
                        //客户端退出
                        break;
                    }
                    else
                    {
                        //服务器退出
                        await Task.Delay(1000, CancellationToken.None);
                        if (!cancellationToken.IsCancellationRequested)
                        {
                            await Task.Delay(2000, cancellationToken);
                        }
                        break;
                    }
                }
            }
            catch (Exception ex)
            {
                throw new RpcException(new Status(StatusCode.Unknown, "StreamHandler", ex));
            }
            finally
            {
                GlobalState.Sockets.TryRemove(connectionId, out var _);
            }
        }

        private static async Task HandlerClientAsync(ConnectionContext connectionContext, IAsyncStreamReader<HttpData> requestStream, CancellationToken cancellationToken)
        {
            //读取客户端数据
            await foreach (var item in requestStream.ReadAllAsync(cancellationToken))
            {
                await connectionContext.Transport.Output.WriteAsync(item.Payload.Memory, cancellationToken);
            }
        }

        private static async Task HandlerServerAsync(ConnectionContext connectionContext, IServerStreamWriter<HttpData> responseStream, CancellationToken cancellationToken)
        {
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