using Google.Protobuf;
using Google.Protobuf.WellKnownTypes;
using Grpc.Core;
using Hello;
using Microsoft.AspNetCore.Server.Kestrel.Transport.Sockets;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.Logging;
using Microsoft.Net.Http.Headers;
using ServerWebApplication.Common;
using System;
using System.Threading;
using System.Threading.Tasks;

namespace ServerWebApplication.Impl
{

    public class ProcessImpl : Hello.ProcessGrpc.ProcessGrpcBase
    {
        private readonly ILogger<ProcessImpl> logger;
        private readonly SocketConnectionContextFactory connectionFactory;
        private readonly DnsParserService dnsParserService;
        private readonly IMemoryCache memoryCache;
        private readonly CertificatePassword clientPassword;

        public ProcessImpl(ILogger<ProcessImpl> logger,
            SocketConnectionContextFactory connectionFactory,
            DnsParserService dnsParserService,
            IMemoryCache memoryCache,
            CertificatePassword clientPassword)
        {
            this.logger = logger;
            this.dnsParserService = dnsParserService;
            this.memoryCache = memoryCache;
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

        public override async Task<Empty> ConnectToWebServer(ConnectToWebServerRequest request, ServerCallContext context)
        {
            CheckPassword(context);
            try
            {
                SocketConnect target = new SocketConnect(connectionFactory);
                var ipAddress = await dnsParserService.ParseIpAddressAsync(request.Address, context.CancellationToken);

                await target.ConnectAsync(ipAddress, request.Port, context.CancellationToken);
                logger.LogInformation($"成功连接到： {request.Address}:{request.Port}");

                memoryCache.Set(request.BrowserConnectId,
                    target,
                    TimeSpan.FromMinutes(1));

                return new Empty();
            }
            catch (Exception ex)
            {
                throw new RpcException(new Status(StatusCode.Unavailable, $"连接到Web服务器失败: {request.Address}:{request.Port} {ex.InnerException?.Message ?? ex.Message}"));
            }
        }



        public override async Task StreamingServer(IAsyncStreamReader<SendDataRequest> requestStream,
            IServerStreamWriter<SendDataRequest> responseStream,
            ServerCallContext context)
        {
            CheckPassword(context);

            var browserConnectId = context.RequestHeaders.GetValue("BrowserConnectId");
            ArgumentException.ThrowIfNullOrWhiteSpace(browserConnectId);

            if (!memoryCache.TryGetValue<SocketConnect>(browserConnectId, out var target))
            {
                return;
            }

            //CurrentCount.Inc();
            //logger.LogInformation($"{DateTimeOffset.Now.ToString("HH:mm:ss")} +当前连接数:{CurrentCount.Value}");

            using CancellationTokenSource targetCancelTokenSource = new CancellationTokenSource();
            using CancellationTokenSource cancellationTokenSource = CancellationTokenSource
                .CreateLinkedTokenSource(context.CancellationToken, targetCancelTokenSource.Token);
            var cancelToken = cancellationTokenSource.Token;

            try
            {
                //发到网站服务器
                var task1 = Task.Run(async () =>
                {
                    try
                    {
                        // CurrentTask1Count.Inc();
                        await foreach (var message in requestStream.ReadAllAsync(cancelToken))
                        {
                            await target.SendAsync(message.Data.Memory, cancelToken);
                        }
                    }
                    finally
                    {
                        //CurrentTask1Count.Dec();
                    }
                }, cancelToken);


                var task2 = Task.Run(async () =>
                {
                    try
                    {
                        //CurrentTask2Count.Inc();

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
                        //CurrentTask2Count.Dec();
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
                //CurrentCount.Dec();
                //logger.LogInformation($"{DateTimeOffset.Now.ToString("HH:mm:ss")} -当前连接数:{CurrentCount.Value}");

                await target.DisposeAsync();

                //从缓存中移除
                memoryCache.Remove(browserConnectId);

                logger.LogInformation("StreamingServer结束");
            }
        }


        public override Task<ServerInfoRes> GetServerInfo(Empty request, ServerCallContext context)
        {
            CheckPassword(context);

            ServerInfoRes serverInfoRes = new ServerInfoRes
            {
                //ConnectionCount = (uint)CurrentCount.Value,
                //CurrentTask1Count = (uint)CurrentTask1Count.Value,
                //CurrentTask2Count = (uint)CurrentTask2Count.Value
            };
            return Task.FromResult(serverInfoRes);
        }
    }
}
