using Google.Protobuf;
using Google.Protobuf.WellKnownTypes;
using Grpc.Core;
using Hello;
using Microsoft.AspNetCore.Connections;
using Microsoft.Extensions.Options;
using Microsoft.Net.Http.Headers;
using Prometheus;
using ServerWebApplication.Common;
using ServerWebApplication.Common.DnsHelper;
using ServerWebApplication.Options;
using System.Net;

namespace ServerWebApplication.Impl
{
    public class ProcessImpl(ILogger<ProcessImpl> logger,
        IConnectionFactory connectionFactory,
        CertificatePassword clientPassword,
        IHostApplicationLifetime hostApplicationLifetime,
        DnsParseService dnsParseService,
        IOptions<TransportOptions> transportOptions) : ProcessGrpc.ProcessGrpcBase
    {
        private readonly TransportOptions transportOptionsValue = transportOptions.Value;

        public static readonly Gauge CurrentCount = Metrics
            .CreateGauge("grpc_stream_clients", "GRPC双向流连接数");

        public static readonly Gauge CurrentTask1Count = Metrics
            .CreateGauge("grpc_stream_clients_task1", "GRPC双向流Task1连接数");

        public static readonly Gauge CurrentTask2Count = Metrics
            .CreateGauge("grpc_stream_clients_task2", "GRPC双向流Task2连接数");

        private void CheckPassword(ServerCallContext context)
        {
            var ipAddress = context.GetHttpContext().Connection.RemoteIpAddress;
            if (ipAddress != null && IPAddress.IsLoopback(ipAddress))
            {
                return;
            }

            var password = context.RequestHeaders.GetValue(HeaderNames.Authorization)
               ?.Replace("Password ", string.Empty);
            if (string.IsNullOrWhiteSpace(password) || !string.Equals(password, clientPassword.Password, StringComparison.Ordinal))
            {
                throw new RpcException(new Status(StatusCode.Unauthenticated, string.Empty));
            }
        }

        public override async Task StreamingServer(IAsyncStreamReader<SendDataRequest> requestStream,
            IServerStreamWriter<SendDataRequest> responseStream,
            ServerCallContext context)
        {
            CheckPassword(context);

            var targetAddress = context.RequestHeaders.GetValue("TargetAddress");
            ArgumentException.ThrowIfNullOrWhiteSpace(targetAddress, nameof(targetAddress));
            var targetPortStr = context.RequestHeaders.GetValue("TargetPort");
            ArgumentException.ThrowIfNullOrWhiteSpace(targetPortStr, nameof(targetPortStr));
            if (!int.TryParse(targetPortStr, out var targetPort))
            {
                throw new InvalidDataException($"TargetPort={targetPortStr} Invalid");
            }

            using var cancellationSource = CancellationTokenSource.CreateLinkedTokenSource(
                  context.CancellationToken, hostApplicationLifetime.ApplicationStopping);
            var cancellationToken = cancellationSource.Token;

            Logs.StartConnect(logger, targetAddress, targetPort);
            await using SocketConnect target = new(connectionFactory, logger, dnsParseService);
            await target.ConnectAsync(targetAddress, targetPort, cancellationToken);
            Logs.SuccessConnect(logger, targetAddress, targetPort);

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
                await foreach (var item in Task.WhenEach(taskClient, taskServer))
                {
                    if (item.Exception != null)
                    {
                        foreach (var error in item.Exception.InnerExceptions)
                        {
                            Logs.RunException(logger, targetAddress, targetPort, error.Message, error.StackTrace);
                        }
                    }

                    if (item.Id == taskClient.Id)
                    {
                        break;
                    }
                    else if (item.Id == taskServer.Id)
                    {
                        await Task.Delay(500, CancellationToken.None);
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
                Logs.RunException(logger, targetAddress, targetPort, ex.Message, ex.StackTrace);
            }
            finally
            {
                CurrentCount.Dec();
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

        private async Task HandlerServer(IServerStreamWriter<SendDataRequest> responseStream,
            SocketConnect target, CancellationToken cancellationToken)
        {
            if (target.PipeReader == null)
            {
                return;
            }

            try
            {
                CurrentTask2Count.Inc();

                if (transportOptionsValue.UseMax4096Bytes)
                {
                    //慢速模式 每次最多4096个字节
                    //从目标服务器读取数据，发送到客户端
                    await foreach (var memory in DotNext.IO.Pipelines.PipeExtensions.ReadAllAsync(target.PipeReader, cancellationToken))
                    {
                        //写入到数据通道
                        await responseStream.WriteAsync(new SendDataRequest
                        {
                            Data = UnsafeByteOperations.UnsafeWrap(memory)
                        }, cancellationToken);
                    }
                }
                else
                {
                    //快速模式
                    //从目标服务器读取数据，发送到客户端
                    await foreach (var memory in PipeReaderExtend.ReadAllAsync(target.PipeReader, cancellationToken))
                    {
                        //写入到数据通道
                        await responseStream.WriteAsync(new SendDataRequest
                        {
                            Data = UnsafeByteOperations.UnsafeWrap(memory)
                        }, cancellationToken);
                    }
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
