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
            var requestStreamCompleted = false;
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

                if (completeTask == taskClient && taskClient.Status == TaskStatus.RanToCompletion)
                {
                    //浏览器正常发完：立刻向服务器半关闭（补 FIN），让服务器知道请求已经发完。
                    //不能拖到最后再补——那样服务器要等收尾窗口结束才知道，它的大响应会被截断
                    try
                    {
                        await duplexStreamingCall.RequestStream.CompleteAsync();
                        requestStreamCompleted = true;
                    }
                    catch (Exception ex) when (ex is RpcException or InvalidOperationException or OperationCanceledException)
                    {
                        //RPC 已经结束，半关闭没有意义
                    }
                }

                if (requestStreamCompleted)
                {
                    //服务器此刻才知道请求发完，它的响应可能比"浏览器发完"晚任意长时间，
                    //所以必须一直等到服务器关闭或浏览器断开。这里加超时就是截断数据
                    await Task.WhenAny(todoTask, Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken));
                }
                else
                {
                    //其余情况（服务器先关闭、浏览器异常、取消）：另一个方向已经没有可送达的数据，给个收尾窗口即可
                    await Task.WhenAny(todoTask, Task.Delay(TimeSpan.FromSeconds(5), cancellationToken));
                }

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

                //已经半关闭过就不再补，避免收尾阶段抛异常盖住真正的结果
                if (!requestStreamCompleted)
                {
                    try
                    {
                        await duplexStreamingCall.RequestStream.CompleteAsync();
                    }
                    catch (Exception ex) when (ex is RpcException or InvalidOperationException or OperationCanceledException or ObjectDisposedException)
                    {
                        //RPC 可能已经结束
                    }
                }
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
