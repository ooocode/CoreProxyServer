using CoreProxy.Server.Orleans.Internal;
using CoreProxy.Server.Orleans.Models;
using DotNext.IO.Pipelines;
using Google.Protobuf;
using Google.Protobuf.WellKnownTypes;
using Grpc.Core;
using Hello;
using Microsoft.AspNetCore.Server.Kestrel.Transport.Sockets;
using Microsoft.AspNetCore.SignalR;
using Microsoft.Net.Http.Headers;
using System.Net;
using System.Threading.Channels;

namespace CoreProxy.Server.Orleans.Services
{
    public class MyGrpcService(
        IHostApplicationLifetime hostApplicationLifetime,
        SocketConnectionContextFactory connectionFactory,
        CertificatePassword certificatePassword,
        ILogger<MyGrpcService> logger,
        IHubContext<ChatHub> hubContext) : Greeter.GreeterBase
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

        private static readonly HttpData EmptyHttpData = new() { Payload = ByteString.Empty };

        public override async Task StreamHandler(IAsyncStreamReader<HttpData> requestStream, IServerStreamWriter<HttpData> responseStream, ServerCallContext context)
        {
            CheckPassword(context);
            var uriString = context.RequestHeaders.GetValue(HeaderNames.XRequestedWith);
            if (string.IsNullOrWhiteSpace(uriString))
            {
                throw new RpcException(new Status(StatusCode.InvalidArgument, string.Empty));
            }

            string connectionId = Guid.CreateVersion7().ToString("N");

            using var cancellationSource = CancellationTokenSource.CreateLinkedTokenSource(
                        context.CancellationToken, hostApplicationLifetime.ApplicationStopping);
            cancellationSource.CancelAfter(TimeSpan.FromHours(1));
            var cancellationToken = cancellationSource.Token;

            try
            {
                Uri uri = new(uriString);
                var host = uri.Host;
                var port = uri.Port;

                //添加连接信息
                GlobalState.Sockets.TryAdd(connectionId, new ConnectItem
                {
                    ClientIpAddress = context.Peer,
                    DateTime = DateTimeOffset.UtcNow
                });

                await using TcpConnectTargetServerService tcpConnectTargetServerService = new(connectionFactory, host, port);
                await tcpConnectTargetServerService.ConnectAsync(cancellationToken);

                //发送空包，表示连接成功
                await responseStream.WriteAsync(EmptyHttpData, cancellationToken);

                var taskClient = HandlerClientAsync(tcpConnectTargetServerService, requestStream, cancellationToken);
                var taskServer = HandlerServerAsync(tcpConnectTargetServerService, responseStream, cancellationToken);

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
            catch (UriFormatException)
            {
                throw new RpcException(new Status(StatusCode.InvalidArgument, uriString));
            }
            catch (Exception ex)
            {
                throw new RpcException(new Status(StatusCode.Cancelled, "StreamHandler", ex));
            }
            finally
            {
                GlobalState.Sockets.TryRemove(connectionId, out var _);
                if (!cancellationSource.IsCancellationRequested)
                {
                    cancellationSource.Cancel();
                }
            }
        }

        private static async Task HandlerClientAsync(TcpConnectTargetServerService tcpConnectTargetServerService, IAsyncStreamReader<HttpData> requestStream, CancellationToken cancellationToken)
        {
            //读取客户端数据
            await foreach (var item in requestStream.ReadAllAsync(cancellationToken))
            {
                await tcpConnectTargetServerService.SendAsync(item.Payload.Memory, cancellationToken);
            }
        }

        private static async Task HandlerServerAsync(TcpConnectTargetServerService tcpConnectTargetServerService, IServerStreamWriter<HttpData> responseStream, CancellationToken cancellationToken)
        {
            //读取目标服务器数据
            await foreach (var item in tcpConnectTargetServerService.ReceiveAsync(cancellationToken))
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

        /// <summary>
        /// p2p
        /// </summary>
        /// <param name="requestStream"></param>
        /// <param name="responseStream"></param>
        /// <param name="context"></param>
        /// <returns></returns>
        public override async Task P2PStreamHandler(IAsyncStreamReader<HttpData> requestStream, IServerStreamWriter<HttpData> responseStream, ServerCallContext context)
        {
            string? current = context.RequestHeaders.GetValue("current-user")?.Trim();
            ArgumentException.ThrowIfNullOrWhiteSpace(current, nameof(current));

            string? target = context.RequestHeaders.GetValue("target-user")?.Trim();
            ArgumentException.ThrowIfNullOrWhiteSpace(target, nameof(target));

            string? currentRole = context.RequestHeaders.GetValue("current-role")?.Trim();
            ArgumentException.ThrowIfNullOrWhiteSpace(currentRole, nameof(currentRole));

            if (string.Compare(current, target, true) == 0)
            {
                throw new RpcException(new Status(StatusCode.Cancelled, "当前和目标不能相同"));
            }

            if (!GloableSessionsManager.SignalrOnlineClients.TryGetValue(current, out var _))
            {
                throw new RpcException(new Status(StatusCode.Cancelled, $"{current}没有signalr在线"));
            }

            if (!GloableSessionsManager.SignalrOnlineClients.TryGetValue(target, out var targetDevice))
            {
                throw new RpcException(new Status(StatusCode.Cancelled, $"{target}没有signalr在线"));
            }

            using var cancellationSource = CancellationTokenSource.CreateLinkedTokenSource(
                        context.CancellationToken, hostApplicationLifetime.ApplicationStopping);
            cancellationSource.CancelAfter(TimeSpan.FromHours(1));
            var cancellationToken = cancellationSource.Token;

            var sessionId = string.Join('^', new List<string> { current.ToUpper(), target.ToUpper() }.Order());

            try
            {
                Channel<ReadOnlyMemory<byte>>? channelReader = null;
                Channel<ReadOnlyMemory<byte>>? channelWrite = null;

                if (string.Compare(currentRole, "master", true) == 0)
                {
                    //主控 创建会话
                    var sessionInfo = new SessionInfo()
                    {
                        Creator = current,
                        ChannelA = Channel.CreateUnbounded<ReadOnlyMemory<byte>>(),
                        ChannelB = Channel.CreateUnbounded<ReadOnlyMemory<byte>>(),
                    };

                    if (!GloableSessionsManager.SessionList.TryAdd(sessionId, sessionInfo))
                    {
                        throw new Exception("重复创建会话");
                    }

                    //通知对方加入
                    logger.LogInformation($"开始调用SignalR客户端：JoinSession({target},{current},slave)");
                    await hubContext.Clients.Client(targetDevice.ConnectionId).InvokeAsync<string>(
                        "JoinSession", target, current, "slave", cancellationToken);

                    channelReader = sessionInfo.ChannelB;
                    channelWrite = sessionInfo.ChannelA;
                }
                else if (string.Compare(currentRole, "slave", true) == 0)
                {
                    //被控 加入会话
                    if (!GloableSessionsManager.SessionList.TryGetValue(sessionId, out var sessionInfo))
                    {
                        throw new Exception("不存在会话");
                    }

                    channelReader = sessionInfo.ChannelA;
                    channelWrite = sessionInfo.ChannelB;
                }
                else
                {
                    throw new Exception($"不支持的角色类型{currentRole}");
                }

                //开始读写循环
                var taskWrite = HandlerChannelWrite(requestStream, channelWrite, cancellationToken);
                var taskRead = HandlerChannelReader(channelReader, responseStream, cancellationToken);
                await Task.WhenAny(taskWrite, taskRead);
            }
            finally
            {
                if (GloableSessionsManager.SessionList.TryRemove(sessionId, out var s))
                {
                    s.ChannelA.Writer.Complete();
                    s.ChannelB.Writer.Complete();
                }
                if (!cancellationSource.IsCancellationRequested)
                {
                    cancellationSource.Cancel();
                }
            }
        }

        private static async Task HandlerChannelWrite(IAsyncStreamReader<HttpData> requestStream, Channel<ReadOnlyMemory<byte>> channelWrite, CancellationToken cancellationToken)
        {
            await foreach (var item in requestStream.ReadAllAsync(cancellationToken))
            {
                await channelWrite.Writer.WriteAsync(item.Payload.Memory, cancellationToken);
            }
        }

        private static async Task HandlerChannelReader(Channel<ReadOnlyMemory<byte>> channelReader, IServerStreamWriter<HttpData> responseStream, CancellationToken cancellationToken)
        {
            await foreach (var item in channelReader.Reader.ReadAllAsync(cancellationToken))
            {
                await responseStream.WriteAsync(new HttpData
                {
                    Payload = UnsafeByteOperations.UnsafeWrap(item)
                }, cancellationToken);
            }
        }

        public override Task<SignalrOnlineClients> GetSignalrOnlineClients(Empty request, ServerCallContext context)
        {
            var result = new SignalrOnlineClients();
            result.Clients.AddRange(GloableSessionsManager.SignalrOnlineClients.Values);
            return Task.FromResult(result);
        }
    }
}