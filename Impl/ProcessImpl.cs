﻿using Google.Protobuf;
using Google.Protobuf.WellKnownTypes;
using Grpc.Core;
using Hello;
using Microsoft.AspNetCore.Connections;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Net.Http.Headers;
using Prometheus;
using ServerWebApplication.Common;
using System;
using System.Threading;
using System.Threading.Tasks;

namespace ServerWebApplication.Impl
{
    public class ProcessImpl(ILogger<ProcessImpl> logger,
        IConnectionFactory connectionFactory,
        CertificatePassword clientPassword,
         IHostApplicationLifetime hostApplicationLifetime) : ProcessGrpc.ProcessGrpcBase
    {
        public static Gauge CurrentCount = Metrics
            .CreateGauge("grpc_stream_clients", "GRPC双向流连接数");

        public static Gauge CurrentTask1Count = Metrics
            .CreateGauge("grpc_stream_clients_task1", "GRPC双向流Task1连接数");

        public static Gauge CurrentTask2Count = Metrics
            .CreateGauge("grpc_stream_clients_task2", "GRPC双向流Task2连接数");

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


        private async ValueTask<SocketConnect> CreateSocketConnectAsync(string address, int port, CancellationToken cancellationToken)
        {
            try
            {
                SocketConnect target = new(connectionFactory);
                await target.ConnectAsync(address, port, cancellationToken);
                logger.LogInformation($"成功连接到： {address}:{port}");
                return target;
            }
            catch (Exception ex)
            {
                logger.LogError($"无法连接到： {address}:{port}  {ex.InnerException?.Message ?? ex.Message}");
                throw;
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
            await using var target = await CreateSocketConnectAsync(targetAddress, targetPort, cancellationToken);

            //返回成功
            await responseStream.WriteAsync(new SendDataRequest
            {
                Data = ByteString.Empty
            }, cancellationToken);

            CurrentCount.Inc();

            try
            {
                //单核CPU
                var taskClient = HandlerClient(requestStream, target, cancellationToken);
                var taskServer = HandlerServer(responseStream, target, cancellationToken);
                await foreach (var item in Task.WhenEach(taskClient, taskServer).WithCancellation(cancellationToken))
                {
                    if (item.Id == taskClient.Id)
                    {
                        break;
                    }
                    else if (item.Id == taskServer.Id)
                    {
                        logger.LogError($"服务器主动断开:{targetAddress}:{targetPort}");
                        await Task.Delay(1500, cancellationToken);
                        break;
                    }
                }
            }
            catch (TaskCanceledException)
            {
                logger.LogWarning("连接已经取消:" + targetAddress + ":" + targetPort);
            }
            finally
            {
                CurrentCount.Dec();
            }
        }

        private static async Task HandlerClient(IAsyncStreamReader<SendDataRequest> requestStream, SocketConnect target, CancellationToken cancellationToken)
        {
            try
            {
                CurrentTask1Count.Inc();
                await foreach (var message in requestStream.ReadAllAsync(cancellationToken).WithCancellation(cancellationToken))
                {
                    await target.SendAsync(message.Data.Memory, cancellationToken);
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
            try
            {
                CurrentTask2Count.Inc();

                //从目标服务器读取数据，发送到客户端
                await foreach (var memory in target.LoopRecvDataAsync(cancellationToken).WithCancellation(cancellationToken))
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
