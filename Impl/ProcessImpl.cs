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
        IOptions<TransportOptions> transportOptions) : ProcessGrpc.ProcessGrpcBase
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

        /// <summary>
        /// 从头部解析出目标地址和端口
        /// </summary>
        /// <param name="context"></param>
        /// <returns></returns>
        private (string, int) ParseTargetAddressAndPort(ServerCallContext context)
        {
            var targetBase64 = context.RequestHeaders.GetValue("Target");
            ArgumentException.ThrowIfNullOrWhiteSpace(targetBase64, nameof(targetBase64));

            var targetBytes = Convert.FromBase64String(targetBase64);

            string target;
            if (transportOptionsValue.EnableDataEncrypt)
            {
                using var rawBytes = Aes256GcmEncryptService.Decrypt(clientPassword.PasswordKey.Span, targetBytes);
                target = Encoding.UTF8.GetString(rawBytes.Span);
            }
            else
            {
                target = Encoding.UTF8.GetString(targetBytes);
            }

            var arr = target.Split('^');
            return new(arr[0], int.Parse(arr[1]));
        }

        private static readonly SendDataRequest EmptySendDataRequest = new() { Data = ByteString.Empty };

        /// <summary>
        /// 双向流处理
        /// </summary>
        /// <param name="requestStream"></param>
        /// <param name="responseStream"></param>
        /// <param name="context"></param>
        /// <returns></returns>
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
            await responseStream.WriteAsync(EmptySendDataRequest, cancellationToken);

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
                    if (message.Data.Length == 0)
                    {
                        break;
                    }

                    if (transportOptionsValue.EnableDataEncrypt)
                    {
                        //解密后发往目标服务器
                        using var bytes = Aes256GcmEncryptService.Decrypt(clientPassword.PasswordKey.Span, message.Data.Span);
                        await target.PipeWriter.WriteAsync(bytes.Memory, cancellationToken);
                    }
                    else
                    {
                        //直接发送到目标服务器
                        await target.PipeWriter.WriteAsync(message.Data.Memory, cancellationToken);
                    }
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

                await foreach (var memory in PipeReaderExtend.ReadAllAsync(transportOptionsValue.UseMax4096Bytes, target.PipeReader, cancellationToken))
                {
                    if (memory.Length == 0)
                    {
                        break;
                    }

                    if (transportOptionsValue.EnableDataEncrypt)
                    {
                        //加密后发往客户端
                        using var encrytData = Aes256GcmEncryptService.Encrypt(clientPassword.PasswordKey.Span, memory.Span);
                        await responseStream.WriteAsync(new SendDataRequest
                        {
                            Data = UnsafeByteOperations.UnsafeWrap(encrytData.Memory)
                        }, cancellationToken);
                    }
                    else
                    {
                        //直接发往客户端
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
                ConnectingCount = (int)CurrentConnectingCount.Value,
                ConnectionCount = (int)CurrentCount.Value,
                CurrentTask1Count = (int)CurrentTask1Count.Value,
                CurrentTask2Count = (int)CurrentTask2Count.Value
            };

            return Task.FromResult(serverInfoRes);
        }
    }
}
