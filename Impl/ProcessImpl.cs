using Google.Protobuf;
using Google.Protobuf.WellKnownTypes;
using Grpc.Core;
using Hello;
using Microsoft.AspNetCore.Server.Kestrel.Transport.Sockets;
using Microsoft.Extensions.Logging;
using Microsoft.Net.Http.Headers;
using Prometheus;
using ServerWebApplication.Common;
using System;
using System.Threading;
using System.Threading.Tasks;

namespace ServerWebApplication.Impl
{
    public class ProcessImpl : ProcessGrpc.ProcessGrpcBase
    {
        private readonly ILogger<ProcessImpl> logger;
        private readonly SocketConnectionContextFactory connectionFactory;
        private readonly CertificatePassword clientPassword;

        public static Gauge CurrentCount = Metrics
            .CreateGauge("grpc_stream_clients", "GRPC双向流连接数");

        public static Gauge CurrentTask1Count = Metrics
            .CreateGauge("grpc_stream_clients_task1", "GRPC双向流Task1连接数");

        public static Gauge CurrentTask2Count = Metrics
            .CreateGauge("grpc_stream_clients_task2", "GRPC双向流Task2连接数");


        public ProcessImpl(ILogger<ProcessImpl> logger,
            SocketConnectionContextFactory connectionFactory,
            CertificatePassword clientPassword)
        {
            this.logger = logger;
            this.clientPassword = clientPassword;
            this.connectionFactory = connectionFactory;
        }

        private void CheckPassword(ServerCallContext context)
        {
            var password = context.RequestHeaders.GetValue(HeaderNames.Authorization)
                ?.Replace("Password ", string.Empty);
            if (string.IsNullOrWhiteSpace(password) || password != clientPassword.Password)
            {
#if !DEBUG
                throw new RpcException(new Status(StatusCode.Unauthenticated, string.Empty));
#endif
            }
        }

        public override async Task StreamingServer(IAsyncStreamReader<SendDataRequest> requestStream,
            IServerStreamWriter<SendDataRequest> responseStream,
            ServerCallContext context)
        {
            CheckPassword(context);

            var targetAddress = context.RequestHeaders.GetValue("TargetAddress");
            var targetPort = int.Parse(context.RequestHeaders.GetValue("TargetPort")!);

            await using var target = await CreateSocketConnectAsync(targetAddress!, targetPort, context.CancellationToken);

            //返回成功
            await responseStream.WriteAsync(new SendDataRequest
            {
                Data = ByteString.Empty
            }, context.CancellationToken);

            //using var cancellationTokenSource = new CancellationTokenSource();
            //using var combineSource = CancellationTokenSource.CreateLinkedTokenSource(
            //    context.CancellationToken, cancellationTokenSource.Token);

            //var cancellationToken = combineSource.Token;
            var cancellationToken = context.CancellationToken;

            try
            {
                CurrentCount.Inc();
                var task1 = LoopReadClient(requestStream, target, cancellationToken);
                var task2 = LoopReadServer(responseStream, target, cancellationToken);

                await foreach (var item in TaskEx.WhenEach([task1, task2]))
                {
                    if (item == nameof(LoopReadClient))
                    {
                        //客户端退出了, 立即退出
                        break;
                    }
                    else if (item == nameof(LoopReadServer))
                    {
                        //服务器端退出了，等1秒钟后退出，避免数据丢失
                        await Task.Delay(1000, cancellationToken);
                        break;
                    }
                }
            }
            catch (TaskCanceledException)
            {
                logger.LogInformation("Grpc客户端关闭了流");
            }
            catch (Exception ex)
            {
                logger.LogError(ex, "StreamingServer");
            }
            finally
            {
                CurrentCount.Dec();
                logger.LogInformation("StreamingServer结束");
            }
        }

        private async Task<SocketConnect> CreateSocketConnectAsync(string address, int port, CancellationToken cancellationToken)
        {
            SocketConnect target = new SocketConnect(connectionFactory);
            await target.ConnectAsync(address, port, cancellationToken);
            logger.LogInformation($"成功连接到： {address}:{port}");
            return target;
        }

        private static async Task<string> LoopReadClient(
            IAsyncStreamReader<SendDataRequest> requestStream,
            SocketConnect target,
            CancellationToken cancellationToken)
        {
            try
            {
                CurrentTask1Count.Inc();
                await foreach (var message in requestStream.ReadAllAsync(cancellationToken))
                {
                    //发到网站服务器
                    await target.SendAsync(message.Data.Memory, cancellationToken);
                }
            }
            finally
            {
                CurrentTask1Count.Dec();
            }
            return nameof(LoopReadClient);
        }

        private static async Task<string> LoopReadServer(
            IServerStreamWriter<SendDataRequest> responseStream,
            SocketConnect target,
            CancellationToken cancellationToken)
        {
            try
            {
                CurrentTask2Count.Inc();

                //从目标服务器读取数据，发送到客户端
                await foreach (var memory in target.LoopRecvDataAsync(cancellationToken))
                {
                    var req = new SendDataRequest
                    {
                        Data = UnsafeByteOperations.UnsafeWrap(memory)
                    };
                    //写入到数据通道
                    await responseStream.WriteAsync(req, cancellationToken);
                }
            }
            finally
            {
                CurrentTask2Count.Dec();
            }
            return nameof(LoopReadServer);
        }


        public override Task<ServerInfoRes> GetServerInfo(Empty request, ServerCallContext context)
        {
            CheckPassword(context);

            ServerInfoRes serverInfoRes = new ServerInfoRes
            {
                ConnectionCount = (uint)CurrentCount.Value,
                CurrentTask1Count = (uint)CurrentTask1Count.Value,
                CurrentTask2Count = (uint)CurrentTask2Count.Value
            };
            return Task.FromResult(serverInfoRes);
        }
    }
}
