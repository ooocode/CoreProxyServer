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
        private readonly DnsParserService dnsParserService;
        private readonly CertificatePassword clientPassword;

        public static Gauge CurrentCount = Metrics
            .CreateGauge("grpc_stream_clients", "GRPC双向流连接数");

        public static Gauge CurrentTask1Count = Metrics
            .CreateGauge("grpc_stream_clients_task1", "GRPC双向流Task1连接数");

        public static Gauge CurrentTask2Count = Metrics
            .CreateGauge("grpc_stream_clients_task2", "GRPC双向流Task2连接数");


        public ProcessImpl(ILogger<ProcessImpl> logger,
            SocketConnectionContextFactory connectionFactory,
            DnsParserService dnsParserService,
            CertificatePassword clientPassword)
        {
            this.logger = logger;
            this.dnsParserService = dnsParserService;

            this.clientPassword = clientPassword;
            this.connectionFactory = connectionFactory;
        }

        private void CheckPassword(ServerCallContext context)
        {
            var password = context.RequestHeaders.GetValue(HeaderNames.Authorization)
                ?.Replace("Password ", string.Empty);
            if (string.IsNullOrWhiteSpace(password) || password != clientPassword.Password)
            {
                throw new RpcException(new Status(StatusCode.Unauthenticated, string.Empty));
            }
        }


        private async Task<SocketConnect> CreateSocketConnectAsync(string address, int port, CancellationToken cancellationToken)
        {
            SocketConnect target = new SocketConnect(connectionFactory);
            var ipAddress = await dnsParserService.ParseIpAddressAsync(address, cancellationToken);

            await target.ConnectAsync(ipAddress, port, cancellationToken);
            logger.LogInformation($"成功连接到： {address}:{port}");
            return target;
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

            using CancellationTokenSource targetCancelTokenSource = new CancellationTokenSource();
            using CancellationTokenSource cancellationTokenSource = CancellationTokenSource
                .CreateLinkedTokenSource(context.CancellationToken, targetCancelTokenSource.Token);
            var cancelToken = cancellationTokenSource.Token;

            CurrentCount.Inc();
            try
            {
                //发到网站服务器
                var task1 = Task.Run(async () =>
                {
                    try
                    {
                        CurrentTask1Count.Inc();
                        await foreach (var message in requestStream.ReadAllAsync(cancelToken))
                        {
                            await target.SendAsync(message.Data.Memory, cancelToken);
                        }
                    }
                    finally
                    {
                        CurrentTask1Count.Dec();
                    }
                }, cancelToken);


                var task2 = Task.Run(async () =>
                {
                    try
                    {
                        CurrentTask2Count.Inc();

                        //从目标服务器读取数据，发送到客户端
                        await foreach (var memory in target.LoopRecvDataAsync(cancelToken))
                        {
                            //写入到数据通道
                            await responseStream.WriteAsync(new SendDataRequest
                            {
                                Data = UnsafeByteOperations.UnsafeWrap(memory)
                            }, cancelToken);
                        }
                    }
                    finally
                    {
                        CurrentTask2Count.Dec();
                        targetCancelTokenSource.Cancel();
                    }
                }, cancelToken);

                await Task.WhenAny(task1, task2);
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
