using CoreProxy.Server.Orleans.Internal;
using CoreProxy.Server.Orleans.Models;
using DotNext.IO.Pipelines;
using Google.Protobuf;
using Google.Protobuf.WellKnownTypes;
using Grpc.Core;
using Hello;
using Microsoft.AspNetCore.SignalR;
using Microsoft.Extensions.Configuration;
using Microsoft.Net.Http.Headers;
using System.Buffers;
using System.Net;
using System.Net.Sockets;
using System.Threading.Channels;

namespace CoreProxy.Server.Orleans.Services
{
    public class MyGrpcService(
        IHostApplicationLifetime hostApplicationLifetime,
        CertificatePassword certificatePassword,
        ILogger<MyGrpcService> logger,
        IHubContext<ChatHub> hubContext,
        IConfiguration configuration,
        TargetResolver targetResolver) : Greeter.GreeterBase
    {
        /// <summary>
        /// 解析目标 + 建立连接的独立超时。
        /// 不能只依赖客户端取消：目标服务器卡住（不回 SYN/ACK）时，一条隧道会一直挂着，
        /// 实测过一次服务端连某个 CDN 迟迟不返回、客户端侧挂了 4 分钟。
        /// 可用 ConnectTimeoutSeconds 配置覆盖
        /// </summary>
        private readonly TimeSpan connectTimeout =
            int.TryParse(configuration["ConnectTimeoutSeconds"], out var connectTimeoutSeconds) && connectTimeoutSeconds > 0
                ? TimeSpan.FromSeconds(connectTimeoutSeconds)
                : TimeSpan.FromSeconds(20);

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

        public static long GetUnixTimeMilliseconds() => DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();

        /// <summary>
        /// 把 gRPC 的 Peer 字符串（形如 ipv4:127.0.0.1:54321 / ipv6:[::1]:54321，带 scheme 前缀和端口）
        /// 剥成纯 IP 字符串，供管理页直接展示"客户端 IP"，避免前端再去解析前缀/端口。
        /// </summary>
        internal static string? StripPeerToIp(string? peer)
        {
            if (string.IsNullOrWhiteSpace(peer))
            {
                return null;
            }

            var s = peer;
            if (s.StartsWith("ipv4:", StringComparison.Ordinal) || s.StartsWith("ipv6:", StringComparison.Ordinal))
            {
                s = s[(s.IndexOf(':') + 1)..];
            }

            return IPEndPoint.TryParse(s, out var ep) ? ep.Address.ToString() : null;
        }

        public override async Task StreamHandler(IAsyncStreamReader<HttpData> requestStream, IServerStreamWriter<HttpData> responseStream, ServerCallContext context)
        {
            CheckPassword(context);

            //目标地址无效时返回 InvalidArgument，而不是让 Uri 构造函数抛出未知异常
            var uriString = context.RequestHeaders.GetValue(HeaderNames.XRequestedWith);
            if (!Uri.TryCreate(uriString, UriKind.Absolute, out var uri) || uri.DnsSafeHost.Length == 0 || uri.Port <= 0)
            {
                throw new RpcException(new Status(StatusCode.InvalidArgument, HeaderNames.XRequestedWith));
            }

            //DnsSafeHost 会去掉 IPv6 字面量的方括号
            var host = uri.DnsSafeHost;
            var port = uri.Port;

            string connectionId = Guid.CreateVersion7().ToString("N");

            using var cancellationSource = CancellationTokenSource.CreateLinkedTokenSource(
                        context.CancellationToken, hostApplicationLifetime.ApplicationStopping);
            var cancellationToken = cancellationSource.Token;

            //解析目标 + 连接目标服务器（带独立超时）
            await using var target = await ConnectToTargetAsync(host, port, cancellationToken);

            //发送空包，表示连接成功
            await responseStream.WriteAsync(new()
            {
                Payload = ByteString.Empty,
                UnixTimeMilliseconds = GetUnixTimeMilliseconds()
            }, cancellationToken);

            if (logger.IsEnabled(LogLevel.Information))
            {
                logger.LogInformation("开始连接目标服务器 {host}:{port}，ConnectionId:{connectionId}", host, port, connectionId);
            }

            //添加连接信息
            GlobalState.Connections.TryAdd(connectionId, new ConnectItem
            {
                Kind = ConnectionKind.Grpc,
                Peer = context.Peer,                    // 原始串，供 ClientIpAddress 派生保真
                ClientIp = StripPeerToIp(context.Peer), // 纯 IP，供管理页展示
                Target = $"{host}:{port}",
                DateTime = DateTimeOffset.UtcNow
            });

            try
            {
                //客户端循环
                var taskClient = HandlerClientAsync(requestStream, target, cancellationToken);

                //服务器循环
                var taskServer = HandlerServerAsync(target, responseStream, cancellationToken);

                //等待任意一个完成
                var completeTask = await Task.WhenAny(taskClient, taskServer);

                //未完成的任务
                var todoTask = completeTask == taskClient ? taskServer : taskClient;

                if (completeTask == taskClient && taskClient.Status == TaskStatus.RanToCompletion && await taskClient)
                {
                    //客户端正常发完并且已向目标服务器补发 FIN：目标服务器此时才知道请求结束，
                    //它的响应可能比"客户端发完"晚任意长时间，所以必须一直等到目标服务器关闭或客户端取消 RPC。
                    //这里加超时就是截断数据
                    await Task.WhenAny(todoTask, Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken));
                }
                else
                {
                    //其余情况（目标服务器先关闭、写入失败、客户端流异常、取消）：
                    //另一个方向已经没有可送达的数据，给一个收尾窗口即可，不能无限等。
                    //目标服务器半关闭时协议里没有"通知客户端"的手段，只能靠结束 RPC 让它知道流已经结束
                    await Task.WhenAny(todoTask, Task.Delay(TimeSpan.FromSeconds(5), cancellationToken));
                }

                cancellationSource.Cancel();

                await foreach (var item in Task.WhenEach(taskClient, taskServer))
                {
                    try
                    {
                        await item; // 展开任务以捕捉真正的异常
                    }
                    catch (OperationCanceledException)
                    {
                        /* 忽略正常的取消 */
                        if (logger.IsEnabled(LogLevel.Information))
                        {
                            logger.LogInformation("数据流转发取消 - ConnectionId:{connectionId}", connectionId);
                        }
                    }
                    catch (Exception ex)
                    {
                        logger.LogError(ex, "数据流转发异常 - ConnectionId:{connectionId}", connectionId);
                    }
                }
            }
            finally
            {
                GlobalState.Connections.TryRemove(connectionId, out var _);
                cancellationSource.Cancel();

                if (logger.IsEnabled(LogLevel.Information))
                {
                    logger.LogInformation("结束连接目标服务器 ConnectionId:{connectionId}", connectionId);
                }
            }
        }


        /// <summary>
        /// 解析目标地址并建立连接，整个过程受 connectTimeout 约束。
        /// 超时按 Unavailable 返回（可重试），与"客户端主动取消"区分开
        /// </summary>
        /// <param name="host"></param>
        /// <param name="port"></param>
        /// <param name="cancellationToken"></param>
        /// <returns></returns>
        private async Task<TargetConnection> ConnectToTargetAsync(string host, int port, CancellationToken cancellationToken)
        {
            using var timeoutSource = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            timeoutSource.CancelAfter(connectTimeout);

            try
            {
                var ip = await targetResolver.ResolveAsync(host, timeoutSource.Token);
                return await TargetConnection.ConnectAsync(new IPEndPoint(ip, port), timeoutSource.Token);
            }
            catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
            {
                //是本地超时而不是客户端取消：给一个明确、可重试的状态，而不是让它一直挂着
                throw new RpcException(new Status(StatusCode.Unavailable, $"{host}:{port}"));
            }
        }


        /// <summary>
        /// 处理客户端
        /// </summary>
        /// <param name="requestStream"></param>
        /// <param name="target"></param>
        /// <param name="cancellationToken"></param>
        /// <returns>是否已向目标服务器补发 FIN（即客户端是否正常发完）</returns>
        private static async Task<bool> HandlerClientAsync(IAsyncStreamReader<HttpData> requestStream, TargetConnection target, CancellationToken cancellationToken)
        {
            //循环读取客户端数据
            await foreach (var httpData in requestStream.ReadAllAsync(cancellationToken))
            {
                //发往服务器：WriteAsync 返回即数据已经交给内核
                await target.WriteAsync(httpData.Payload.Memory, cancellationToken);
            }

            //客户端正常发完：只关闭发送方向补发 FIN（半关闭），让目标服务器知道请求已结束，接收方向继续排空。
            //写入若失败会抛异常，绝不会走到这里，因此不会把截断的请求当成完整请求
            try
            {
                target.ShutdownSend();
                return true;
            }
            catch (Exception ex) when (ex is SocketException or ObjectDisposedException or InvalidOperationException)
            {
                //连接已被中止或 socket 已释放：目标服务器那边已经收到 FIN 或 RST，无需处理
                return false;
            }
        }

        /// <summary>
        /// 处理服务器：把目标服务器的数据转发给客户端
        /// </summary>
        /// <param name="target"></param>
        /// <param name="responseStream"></param>
        /// <param name="cancellationToken"></param>
        private static async Task HandlerServerAsync(TargetConnection target, IServerStreamWriter<HttpData> responseStream, CancellationToken cancellationToken)
        {
            var buffer = ArrayPool<byte>.Shared.Rent(64 * 1024);
            try
            {
                while (true)
                {
                    var read = await target.ReadAsync(buffer, cancellationToken);
                    if (read == 0)
                    {
                        //目标服务器关闭了发送方向
                        break;
                    }

                    //UnsafeWrap 直接引用缓冲区，只有"WriteAsync 内部立即序列化"这一点成立才安全（Grpc.AspNetCore 如此）
                    await responseStream.WriteAsync(new HttpData
                    {
                        Payload = UnsafeByteOperations.UnsafeWrap(buffer.AsMemory(0, read))
                    }, cancellationToken);
                }
            }
            finally
            {
                ArrayPool<byte>.Shared.Return(buffer);
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

        public override Task<SignalrOnlineClients> GetSignalrOnlineClients(Empty request, ServerCallContext context)
        {
            var result = new SignalrOnlineClients();
            result.Clients.AddRange(GloableSessionsManager.SignalrOnlineClients.Values);
            return Task.FromResult(result);
        }
    }
}