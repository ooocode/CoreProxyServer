using CoreProxy.Server.Orleans.Internal;
using CoreProxy.Server.Orleans.Models;
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
    class LastActivityTime
    {
        public long UnixTimeMilliseconds { get; set; }
    }

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

        private static long GetUnixTimeMilliseconds() => DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();

        public override async Task StreamHandler(IAsyncStreamReader<HttpData> requestStream, IServerStreamWriter<HttpData> responseStream, ServerCallContext context)
        {

            CheckPassword(context);
            var uriString = context.RequestHeaders.GetValue(HeaderNames.XRequestedWith);
            if (string.IsNullOrWhiteSpace(uriString))
            {
                throw new RpcException(new Status(StatusCode.InvalidArgument, HeaderNames.XRequestedWith));
            }

            string connectionId = Guid.CreateVersion7().ToString("N");

            using var cancellationSource = CancellationTokenSource.CreateLinkedTokenSource(
                        context.CancellationToken, hostApplicationLifetime.ApplicationStopping);
            var cancellationToken = cancellationSource.Token;

            try
            {
                Uri uri = new(uriString);
                var host = uri.Host;
                var port = uri.Port;

                if (logger.IsEnabled(LogLevel.Information))
                {
                    logger.LogInformation("开始连接目标服务器 {host}:{port}，ConnectionId:{connectionId}", host, port, connectionId);
                }

                //添加连接信息
                GlobalState.Connections.TryAdd(connectionId, new ConnectItem
                {
                    ClientIpAddress = context.Peer,
                    DateTime = DateTimeOffset.UtcNow
                });

                await using TcpConnectTargetServerService tcpConnectTargetServerService = new(connectionFactory, host, port);
                await tcpConnectTargetServerService.ConnectAsync(cancellationToken);

                //发送空包，表示连接成功
                await responseStream.WriteAsync(new()
                {
                    Payload = ByteString.Empty,
                    UnixTimeMilliseconds = GetUnixTimeMilliseconds()
                }, cancellationToken);

                LastActivityTime lastActivityTime = new()
                {
                    UnixTimeMilliseconds = GetUnixTimeMilliseconds()
                };
                var taskClient = HandlerClientAsync(lastActivityTime, tcpConnectTargetServerService, requestStream, cancellationToken);
                var taskServer = HandlerServerAsync(lastActivityTime, tcpConnectTargetServerService, responseStream, cancellationToken);
                var taskCheck = CheckKeepAliveAsync(lastActivityTime, cancellationToken);

                var completedTask = await Task.WhenAny(taskClient, taskServer, taskCheck);
                if (completedTask.Id != taskCheck.Id && !cancellationToken.IsCancellationRequested)
                {
                    // 等待一小段时间，等待剩余数据处理
                    await Task.Delay(2000, CancellationToken.None);
                }
                cancellationSource.Cancel();

                await foreach (var item in Task.WhenEach(taskClient, taskServer, taskCheck))
                {
                    if (item.Exception is not null)
                    {
                        logger.LogError(item.Exception, "Task.WhenEach异常");
                    }
                }
            }
            finally
            {
                GlobalState.Connections.TryRemove(connectionId, out var _);

                if (logger.IsEnabled(LogLevel.Information))
                {
                    logger.LogInformation("结束连接目标服务器 ConnectionId:{connectionId}", connectionId);
                }
            }
        }

        private static async Task CheckKeepAliveAsync(LastActivityTime lastActivityTime, CancellationToken cancellationToken)
        {
            using var timer = new PeriodicTimer(TimeSpan.FromSeconds(5));
            while (await timer.WaitForNextTickAsync(cancellationToken))
            {
                //检查是否超时 75秒
                if (Math.Abs(GetUnixTimeMilliseconds() - lastActivityTime.UnixTimeMilliseconds) > 75_000)
                {
                    break;
                }
            }
        }

        private static async Task HandlerClientAsync(
            LastActivityTime lastActivityTime,
            TcpConnectTargetServerService tcpConnectTargetServerService, IAsyncStreamReader<HttpData> requestStream, CancellationToken cancellationToken)
        {
            //读取客户端数据
            await foreach (var item in requestStream.ReadAllAsync(cancellationToken))
            {
                lastActivityTime.UnixTimeMilliseconds = GetUnixTimeMilliseconds();
                await tcpConnectTargetServerService.SendAsync(item.Payload.Memory, cancellationToken);
            }
        }

        private static async Task HandlerServerAsync(
            LastActivityTime lastActivityTime,
            TcpConnectTargetServerService tcpConnectTargetServerService, IServerStreamWriter<HttpData> responseStream, CancellationToken cancellationToken)
        {
            //读取目标服务器数据
            await foreach (var item in tcpConnectTargetServerService.ReceiveAsync(cancellationToken))
            {
                long unix = GetUnixTimeMilliseconds();
                lastActivityTime.UnixTimeMilliseconds = unix;
                HttpData httpData = new()
                {
                    Payload = UnsafeByteOperations.UnsafeWrap(item),
                    UnixTimeMilliseconds = unix
                };
                await responseStream.WriteAsync(httpData, cancellationToken);
            }
        }

        public override Task<StatusReply> GetStatus(GetStatusRequest request, ServerCallContext context)
        {
            CheckPassword(context);

            var reply = new StatusReply
            {
                SocketCount = GlobalState.Connections.Count
            };

            if (request.IncludeDetail)
            {
                reply.Connections.AddRange(GlobalState.Connections.Values
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
                    if (logger.IsEnabled(LogLevel.Information))
                    {
                        logger.LogInformation("开始调用SignalR客户端：JoinSession({target},{current},slave)", target, current);
                    }

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