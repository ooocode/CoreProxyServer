using Google.Protobuf;
using Google.Protobuf.WellKnownTypes;
using Grpc.Core;
using Hello;
using Microsoft.AspNetCore.Connections;
using Microsoft.Extensions.Options;
using Microsoft.Net.Http.Headers;
using Prometheus;
using ServerWebApplication.Common;
using ServerWebApplication.Options;
using ServerWebApplication.Services;
using System.Net;
using System.Text;

namespace ServerWebApplication.Impl
{
    public class ProcessImpl(ILogger<ProcessImpl> logger,
        IConnectionFactory connectionFactory,
        CertificatePassword clientPassword,
        IHostApplicationLifetime hostApplicationLifetime,
        DnsParseService dnsParseService,
        IOptions<TransportOptions> transportOptions,
        IEncryptService encryptService) : ProcessGrpc.ProcessGrpcBase
    {
        private readonly TransportOptions transportOptionsValue = transportOptions.Value;

        public static readonly Gauge CurrentConnectingCount = Metrics
          .CreateGauge("grpc_stream_connecting_clients", "GRPC双向流正在连接数");

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
            var targetAddressBytes = encryptService.Decrypt(clientPassword.Password, SendDataRequest.Parser.ParseFrom(ByteString.FromBase64(targetAddressHeader)));
            var targetAddress = Encoding.UTF8.GetString(targetAddressBytes.Span);
            ArgumentException.ThrowIfNullOrWhiteSpace(targetAddress, nameof(targetAddress));

            var targetPortStrHeader = context.RequestHeaders.GetValue("TargetPort");
            ArgumentException.ThrowIfNullOrWhiteSpace(targetPortStrHeader, nameof(targetPortStrHeader));
            var targetPortStrBytes = encryptService.Decrypt(clientPassword.Password, SendDataRequest.Parser.ParseFrom(ByteString.FromBase64(targetPortStrHeader)));
            var targetPortStr = Encoding.UTF8.GetString(targetPortStrBytes.Span);
            if (!int.TryParse(targetPortStr, out var targetPort))
            {
                throw new InvalidDataException($"TargetPort={targetPortStr} Invalid");
            }

            return new(targetAddress, targetPort);
        }

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

            var (targetAddress, targetPort) = ParseTargetAddressAndPort(context);

            using var cancellationSource = CancellationTokenSource.CreateLinkedTokenSource(
                  context.CancellationToken, hostApplicationLifetime.ApplicationStopping);
            var cancellationToken = cancellationSource.Token;

            Logs.StartConnect(logger, targetAddress, targetPort);
            await using SocketConnect target = new(connectionFactory, logger, dnsParseService);

            CurrentConnectingCount.Inc();
            try
            {
                await target.ConnectAsync(targetAddress, targetPort, cancellationToken);
            }
            finally
            {
                CurrentConnectingCount.Dec();
            }

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
                    var bytes = encryptService.Decrypt(clientPassword.Password, message);
                    //发送到目标服务器
                    await target.PipeWriter.WriteAsync(bytes, cancellationToken);
                }
            }
            finally
            {
                CurrentTask1Count.Dec();
            }
        }

        /// <summary>
        /// 处理服务端
        /// </summary>
        /// <param name="responseStream"></param>
        /// <param name="target"></param>
        /// <param name="cancellationToken"></param>
        /// <returns></returns>
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
                        SendDataRequest sendDataRequest = encryptService.Encrypt(clientPassword.Password, memory);
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
                        if (memory.Length == 0)
                        {
                            break;
                        }
                        SendDataRequest sendDataRequest = encryptService.Encrypt(clientPassword.Password, memory);
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
            CheckPassword(context);

            ServerInfoRes serverInfoRes = new()
            {
                ConnectingCount = (int)CurrentConnectingCount.Value,
                ConnectionCount = (int)CurrentCount.Value,
                CurrentTask1Count = (int)CurrentTask1Count.Value,
                CurrentTask2Count = (int)CurrentTask2Count.Value
            };

            SendDataRequest sendDataRequest = encryptService.Encrypt(clientPassword.Password, serverInfoRes.ToByteArray());
            return Task.FromResult(sendDataRequest);
        }
    }
}
