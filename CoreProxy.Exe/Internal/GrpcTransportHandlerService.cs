using CoreProxy.BackgroundServices;
using DotNext;
using DotNext.IO.Pipelines;
using Google.Protobuf;
using Grpc.Core;
using Hello;
using Microsoft.AspNetCore.Connections;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Logging;
using Microsoft.Net.Http.Headers;
using System.IO.Pipelines;
using System.Text;

namespace CoreProxy.Internal
{
    public enum ProxyType
    {
        Socket5,
        Http
    }

    public partial class GrpcTransportHandlerService(
        ILogger<GrpcTransportHandlerService> logger)
    {
        private static readonly byte[] Socket5ConnectOkReturnData = [0x05, 0x00, 0x00, 0x01, 0x7f, 0x00, 0x00, 0x01, 0x1f, 0x40];
        private static readonly byte[] HttpConnectOkReturnData = Encoding.UTF8.GetBytes("HTTP/1.1 200 Connection Established\r\n\r\n");

        [LoggerMessage(Level = LogLevel.Information, Message = "开始连接 {connectionId} {targetAddress}:{targetPort}", EventId = StatusCodes.Status200OK)]
        private static partial void LogStartConnectTo(ILogger logger, string connectionId, string targetAddress, int targetPort);

        [LoggerMessage(Level = LogLevel.Information, Message = "成功连接到 {connectionId} {targetAddress}:{targetPort} 毫秒:{elapsedMilliseconds}", EventId = StatusCodes.Status201Created)]
        private static partial void LogSuccessConnectTo(ILogger logger, string connectionId, string targetAddress, int targetPort, long elapsedMilliseconds);

        [LoggerMessage(Level = LogLevel.Error, Message = "【WhenEach-Item】{targetAddress} {errorMessage}\n{stackTrace}", EventId = StatusCodes.Status400BadRequest)]
        private static partial void LogException(ILogger logger, string targetAddress, string errorMessage, string? stackTrace);

        /// <summary>
        /// 浏览器tcp转发
        /// </summary>
        /// <param name="browser"></param>
        /// <param name="targetUri"></param>
        /// <param name="proxyType"></param>
        /// <param name="tokenSource"></param>
        public async Task DualStreamHandlerAsync(Greeter.GreeterClient greeterClient,
                                                 IDuplexPipe browser,
                                                 string targetUri,
                                                 ProxyType proxyType,
        CancellationTokenSource tokenSource)
        {
            var cancellationToken = tokenSource.Token;
            Metadata entries = new()
            {
                { HeaderNames.XRequestedWith,targetUri }
            };

            logger.LogInformation("开始连接 {server}", targetUri);
            cancellationToken.ThrowIfCancellationRequested();
            using var duplexStreamingCall = greeterClient.StreamHandler(headers: entries, cancellationToken: cancellationToken);
            try
            {
                if (!await duplexStreamingCall.ResponseStream.MoveNext(cancellationToken))
                {
                    return;
                }

                var okData = proxyType == ProxyType.Socket5 ? Socket5ConnectOkReturnData : HttpConnectOkReturnData;
                await browser.Output.WriteAsync(okData, cancellationToken);

                //处理浏览器
                using var readBrowserDataClass = new ReadBrowserDataClass(duplexStreamingCall.RequestStream);
                var taskClient = browser.Input.CopyToAsync(readBrowserDataClass, cancellationToken).AsTask();

                //处理服务器
                var taskServer = ReadServerDataAsync(duplexStreamingCall.ResponseStream, browser, cancellationToken);

                //等待任意一个完成
                var completeTask = await Task.WhenAny(taskClient, taskServer);

                //未完成的任务
                var todoTask = completeTask == taskClient ? taskServer : taskClient;

                //再给最多 5 秒排空剩余数据；若两方向都已结束则立即返回
                await Task.WhenAny(todoTask, Task.Delay(TimeSpan.FromSeconds(5), cancellationToken));


                tokenSource.Cancel();

                await foreach (var item in Task.WhenEach(taskClient, taskServer))
                {
                    try
                    {
                        await item; // 展开任务以捕捉真正的异常
                    }
                    catch (RpcException ex) when (ex.Status.StatusCode == StatusCode.Cancelled)
                    {
                        logger.LogWarning("Grpc取消");
                    }
                    catch (OperationCanceledException)
                    {
                        /* 忽略正常的取消 */
                        logger.LogWarning("操作取消");
                    }
                    catch (Exception ex)
                    {
                        logger.LogError(ex, "数据流转发异常");
                    }
                }
            }
            finally
            {
                tokenSource.Cancel();
                await duplexStreamingCall.RequestStream.CompleteAsync();
            }
        }


        /// <summary>
        /// 读浏览器数据
        /// </summary>
        private class ReadBrowserDataClass : ISupplier<ReadOnlyMemory<byte>, CancellationToken, ValueTask>, IDisposable
        {
            private readonly IClientStreamWriter<HttpData> duplexStreamingCall;

            public ReadBrowserDataClass(IClientStreamWriter<HttpData> duplexStreamingCall)
            {
                Stat.ClientCount.Inc();
                this.duplexStreamingCall = duplexStreamingCall;
            }


            public async ValueTask Invoke(ReadOnlyMemory<byte> readOnlyMemory, CancellationToken cancellationToken)
            {
                //发往服务器
                await duplexStreamingCall.WriteAsync(new HttpData
                {
                    Payload = UnsafeByteOperations.UnsafeWrap(readOnlyMemory)
                }, cancellationToken: cancellationToken);
            }

            public void Dispose()
            {
                Stat.ClientCount.Dec();
            }
        }

        /// <summary>
        /// 读服务器数据
        /// </summary>
        /// <param name="browser"></param>
        /// <param name="cancellationToken"></param>
        private static async Task ReadServerDataAsync(
            IAsyncStreamReader<HttpData> duplexStreamingCall,
            IDuplexPipe browser,
            CancellationToken cancellationToken)
        {
            Stat.ServerCount.Inc();

            try
            {
                //读取客户端数据
                await foreach (var item in duplexStreamingCall.ReadAllAsync(cancellationToken))
                {
                    var flushResult = await browser.Output.WriteAsync(item.Payload.Memory, cancellationToken);
                    if (flushResult.IsCompleted || flushResult.IsCanceled)
                    {
                        break;
                    }
                }
            }
            finally
            {
                Stat.ServerCount.Dec();
            }
        }
    }
}
