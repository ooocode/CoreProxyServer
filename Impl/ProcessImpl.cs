using Google.Protobuf;
using Google.Protobuf.WellKnownTypes;
using Grpc.Core;
using Hello;
using Microsoft.AspNetCore.Connections;
using Microsoft.Extensions.Options;
using Prometheus;
using ServerWebApplication.Common;
using ServerWebApplication.Common.DnsHelper;
using ServerWebApplication.Options;
using System.Text;

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

        private (string, int) ParseTargetAddressAndPort(ServerCallContext context)
        {
            var targetAddressHeader = context.RequestHeaders.GetValue("TargetAddress");
            ArgumentException.ThrowIfNullOrWhiteSpace(targetAddressHeader, nameof(targetAddressHeader));
            var targetAddressBytes = MakeSendDataRequest.Decrypt(clientPassword.Password, SendDataRequest.Parser.ParseFrom(ByteString.FromBase64(targetAddressHeader)));
            var targetAddress = Encoding.UTF8.GetString(targetAddressBytes);
            ArgumentException.ThrowIfNullOrWhiteSpace(targetAddress, nameof(targetAddress));

            var targetPortStrHeader = context.RequestHeaders.GetValue("TargetPort");
            ArgumentException.ThrowIfNullOrWhiteSpace(targetPortStrHeader, nameof(targetPortStrHeader));
            var targetPortStrBytes = MakeSendDataRequest.Decrypt(clientPassword.Password, SendDataRequest.Parser.ParseFrom(ByteString.FromBase64(targetPortStrHeader)));
            var targetPortStr = Encoding.UTF8.GetString(targetPortStrBytes);
            if (!int.TryParse(targetPortStr, out var targetPort))
            {
                throw new InvalidDataException($"TargetPort={targetPortStr} Invalid");
            }

            return new(targetAddress, targetPort);
        }

        public override async Task StreamingServer(IAsyncStreamReader<SendDataRequest> requestStream,
            IServerStreamWriter<SendDataRequest> responseStream,
            ServerCallContext context)
        {
            var (targetAddress, targetPort) = ParseTargetAddressAndPort(context);

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

        /// <summary>
        /// 处理客户端请求
        /// </summary>
        /// <param name="requestStream"></param>
        /// <param name="target"></param>
        /// <param name="cancellationToken"></param>
        /// <returns></returns>
        private async Task HandlerClient(IAsyncStreamReader<SendDataRequest> requestStream, SocketConnect target, CancellationToken cancellationToken)
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
                    var bytes = MakeSendDataRequest.Decrypt(clientPassword.Password, message);
                    await target.PipeWriter.WriteAsync(bytes, cancellationToken);
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
                        SendDataRequest sendDataRequest = MakeSendDataRequest.Encrypt(clientPassword.Password, memory);
                        //写入到数据通道
                        await responseStream.WriteAsync(sendDataRequest, cancellationToken);
                    }
                }
                else
                {
                    //快速模式
                    //从目标服务器读取数据，发送到客户端
                    await foreach (var memory in PipeReaderExtend.ReadAllAsync(target.PipeReader, cancellationToken))
                    {
                        SendDataRequest sendDataRequest = MakeSendDataRequest.Encrypt(clientPassword.Password, memory);
                        //写入到数据通道
                        await responseStream.WriteAsync(sendDataRequest, cancellationToken);
                    }
                }
            }
            finally
            {
                CurrentTask2Count.Dec();
            }
        }

        public override Task<SendDataRequest> GetServerInfo(Empty request, ServerCallContext context)
        {
            ServerInfoRes serverInfoRes = new()
            {
                ConnectionCount = (uint)CurrentCount.Value,
                CurrentTask1Count = (uint)CurrentTask1Count.Value,
                CurrentTask2Count = (uint)CurrentTask2Count.Value
            };

            SendDataRequest sendDataRequest = MakeSendDataRequest.Encrypt(clientPassword.Password, serverInfoRes.ToByteArray());
            return Task.FromResult(sendDataRequest);
        }
    }
}
