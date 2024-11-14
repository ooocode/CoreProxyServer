using Google.Protobuf;
using Google.Protobuf.WellKnownTypes;
using Grpc.Core;
using Hello;
using Microsoft.AspNetCore.Connections;
using Microsoft.Net.Http.Headers;
using Prometheus;
using ServerWebApplication.Common;
using System.Collections.Concurrent;
using System.Net;

namespace ServerWebApplication.Impl
{
    public class ProcessImpl(ILogger<ProcessImpl> logger,
        IConnectionFactory connectionFactory,
        CertificatePassword clientPassword,
        IHostApplicationLifetime hostApplicationLifetime,
        DnsParseService dnsParseService) : ProcessGrpc.ProcessGrpcBase
    {
        public static Gauge CurrentCount = Metrics
            .CreateGauge("grpc_stream_clients", "GRPC双向流连接数");

        public static Gauge CurrentTask1Count = Metrics
            .CreateGauge("grpc_stream_clients_task1", "GRPC双向流Task1连接数");

        public static Gauge CurrentTask2Count = Metrics
            .CreateGauge("grpc_stream_clients_task2", "GRPC双向流Task2连接数");

        public static ConcurrentDictionary<string, string> OnlineClients = new ConcurrentDictionary<string, string>();

        private void CheckPassword(ServerCallContext context)
        {
            var ipAddress = context.GetHttpContext().Connection.RemoteIpAddress;
            if (ipAddress != null && IPAddress.IsLoopback(ipAddress))
            {
                return;
            }

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
            ArgumentException.ThrowIfNullOrWhiteSpace(targetAddress, nameof(targetAddress));
            var targetPort = int.Parse(context.RequestHeaders.GetValue("TargetPort")!);

            using var cancellationSource = CancellationTokenSource.CreateLinkedTokenSource(
                  context.CancellationToken, hostApplicationLifetime.ApplicationStopping);
            var cancellationToken = cancellationSource.Token;

            logger.LogInformation($"开始连接：{targetAddress}:{targetPort}");
            await using SocketConnect target = new(connectionFactory, dnsParseService, logger);
            await target.ConnectAsync(targetAddress, targetPort, cancellationToken);
            logger.LogInformation($"成功连接到：{targetAddress}:{targetPort}");

            //返回成功
            await responseStream.WriteAsync(new SendDataRequest
            {
                Data = ByteString.Empty
            }, cancellationToken);

            CurrentCount.Inc();

            var clientId = context.GetHttpContext().Connection.Id;
            OnlineClients.TryAdd(clientId, $"{targetAddress}:{targetPort}");
            try
            {
                //单核CPU
                var taskClient = HandlerClient(requestStream, target, cancellationToken);
                var taskServer = HandlerServer(responseStream, target, cancellationToken);
                await foreach (var item in Task.WhenEach(taskClient, taskServer))
                {
                    if (item.Exception != null)
                    {
                        logger.LogError(item.Exception, "【WhenEach-Item】" + targetAddress + ":" + targetPort);
                    }

                    if (item.Id == taskClient.Id)
                    {
                        break;
                    }
                    else if (item.Id == taskServer.Id)
                    {
                        await Task.Delay(500);
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
                logger.LogError(ex, "【WhenEach】" + targetAddress + ":" + targetPort);
            }
            finally
            {
                CurrentCount.Dec();
                OnlineClients.TryRemove(clientId, out var _);
            }
        }

        private static async Task HandlerClient(IAsyncStreamReader<SendDataRequest> requestStream, SocketConnect target, CancellationToken cancellationToken)
        {
            if (target.PipeWriter == null)
            {
                return;
            }

            try
            {
                CurrentTask1Count.Inc();
                await foreach (var message in requestStream.ReadAllAsync(cancellationToken))
                {
                    await target.PipeWriter.WriteAsync(message.Data.Memory, cancellationToken);
                    await target.PipeWriter.FlushAsync(cancellationToken);
                }
            }
            finally
            {
                CurrentTask1Count.Dec();
            }
        }

        private static async Task HandlerServer(IServerStreamWriter<SendDataRequest> responseStream,
            SocketConnect target, CancellationToken cancellationToken)
        {
            if (target.PipeReader == null)
            {
                return;
            }

            try
            {
                CurrentTask2Count.Inc();

                //从目标服务器读取数据，发送到客户端
                await foreach (var memory in target.PipeReader.ReadAllAsync(cancellationToken))
                {
                    //写入到数据通道
                    await responseStream.WriteAsync(new SendDataRequest
                    {
                        Data = UnsafeByteOperations.UnsafeWrap(memory)
                    }, cancellationToken);
                }
            }
            finally
            {
                CurrentTask2Count.Dec();
            }
        }

        public override Task<ServerInfoRes> GetServerInfo(Empty request, ServerCallContext context)
        {
            CheckPassword(context);

            ServerInfoRes serverInfoRes = new()
            {
                ConnectionCount = (uint)CurrentCount.Value,
                CurrentTask1Count = (uint)CurrentTask1Count.Value,
                CurrentTask2Count = (uint)CurrentTask2Count.Value
            };
            return Task.FromResult(serverInfoRes);
        }
    }
}
