using CoreProxy.Common;
using CoreProxy.ViewModels;
using DotNext.Buffers.Binary;
using DotNext.IO.Pipelines;
using Grpc.Net.ClientFactory;
using Hello;
using Microsoft.AspNetCore.Connections;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Server.Kestrel.Core.Internal.Http;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using System.Buffers;
using System.IO.Pipelines;
using System.Net;
using System.Runtime.InteropServices;

namespace CoreProxy.Internal
{
    [StructLayout(LayoutKind.Sequential, Pack = 1)]
    public readonly struct Socket5Handshake
    {
        public byte Ver { get; init; }

        //方法的个数
        public byte NMethods { get; init; }
    }


    public class Socket5ConnectionHandlerService(
        GrpcClientFactory grpcClientFactory,
        GrpcTransportHandlerService grpcTransportHandlerService,
        IHostApplicationLifetime hostApplicationLifetime,
        List<ServerItem> serverItems,
        ILogger<Socket5ConnectionHandlerService> logger)
    {
        private static readonly byte[] Bytes50 = [5, 0];

        public async Task OnConnectedAsync(ConnectionContext connection, ConnectionDelegate next)
        {
            using var cancellationSource = CancellationTokenSource.CreateLinkedTokenSource(
              connection.ConnectionClosed, hostApplicationLifetime.ApplicationStopping);
            var cancellationToken = cancellationSource.Token;

            //探嗅至少需要 2 个字节（TLS 前缀是 0x16 0x03），这里显式等够再判定；
            //顺带处理"连上就关、一个字节都没发"的情况（旧实现会继续走 SOCKS5 分支）
            ReadResult firstResult;
            do
            {
                firstResult = await connection.Transport.Input.ReadAsync(cancellationToken);
                if (firstResult.IsCompleted && firstResult.Buffer.Length == 0)
                {
                    //对端什么都没发就关了
                    return;
                }
            }
            while (firstResult.Buffer.Length < 2);

            var buffer = firstResult.Buffer;

            var isHttpRequest = IsHttp(buffer);

            // 不消费、不检查、下次立刻拿同样数据
            connection.Transport.Input.AdvanceTo(buffer.Start, buffer.Start);

            if (isHttpRequest)
            {
                //走http
                await next(connection);
                return;
            }

            try
            {

                var handshakeResult = await connection.Transport.Input.ReadAsync<Blittable<Socket5Handshake>>(cancellationToken);
                if (handshakeResult.Value.Ver != 0x05)
                {
                    //不是socket5协议
                    return;
                }

                if (handshakeResult.Value.NMethods == 0)
                {
                    return;
                }

                cancellationToken.ThrowIfCancellationRequested();
                await connection.Transport.Input.SkipAsync(handshakeResult.Value.NMethods, cancellationToken);
                //using var _ = Socket5Utility.ReadBytesAsync(connection.Transport.Input, handshakeResult.Value.NMethods, cancellationToken);
                await HandleSocket5Async(connection, cancellationSource);
            }
            catch (OperationCanceledException)
            {
                //ignore
            }
            catch (Grpc.Core.RpcException ex)
            {
                logger.LogError(ex, "OnConnectedAsync");
            }
            finally
            {
                cancellationSource.Cancel();
            }
        }

        private async Task HandleSocket5Async(ConnectionContext connection, CancellationTokenSource tokenSource)
        {
            //server-->5,0
            //发5 0 回到浏览器
            await connection.Transport.Output.WriteAsync(Bytes50, tokenSource.Token);

            var socket5Result = await Socket5Utility.Parse(connection.Transport.Input, tokenSource.Token);

            var ipEndPoint = connection.LocalEndPoint as IPEndPoint;
            ArgumentNullException.ThrowIfNull(ipEndPoint, nameof(ipEndPoint));

            var serverItem = serverItems.First(x => x.LocalListenerPort == ipEndPoint.Port);

            var processGrpcClient = grpcClientFactory.CreateClient<Greeter.GreeterClient>(serverItem.LocalListenerPort.ToString());
            await grpcTransportHandlerService.DualStreamHandlerAsync(processGrpcClient, connection.Transport,
                socket5Result.Uri, ProxyType.Socket5, tokenSource);
        }


        private static bool IsHttp(ReadOnlySequence<byte> buffer)
        {
            //TLS ClientHello 前缀：0x16(Handshake) + 0x03(TLS 主版本) 表示这是 HTTPS。
            //必须按字节判定：原先靠 BadHttpRequestException 的消息文本比对，
            //那是本地化、版本相关的字符串，一旦 Kestrel 改文案或启用本地化，
            //HTTPS 会静默走 SOCKS5 分支，随后因版本号不是 0x05 被丢弃（表现为"HTTPS 打不开"且无日志）
            var reader = new SequenceReader<byte>(buffer);
            if (reader.TryPeek(0, out var first) && reader.TryPeek(1, out var second)
                && first == 0x16 && second == 0x03)
            {
                return true;
            }

            try
            {
                var handler = new ProbeHandler();
                var parser = new HttpParser<ProbeHandler>();

                //http
                parser.ParseRequestLine(handler, ref reader);
                return true;
            }
            catch (BadHttpRequestException)
            {
                //既不是 TLS 也不是 HTTP
                return false;
            }
        }

        class ProbeHandler : IHttpRequestLineHandler, IHttpHeadersHandler
        {
            public void OnStartLine(HttpVersionAndMethod versionAndMethod, TargetOffsetPathLength targetPath, Span<byte> startLine)
            {

            }

            /// <summary>
            ///  // 只有 HTTP/2 会回调
            /// </summary>
            /// <param name="index"></param>
            /// <exception cref="NotImplementedException"></exception>
            public void OnStaticIndexedHeader(int index)
            {

            }

            /// <summary>
            ///  // 只有 HTTP/2 会回调
            /// </summary>
            /// <param name="index"></param>
            /// <param name="value"></param>
            /// <exception cref="NotImplementedException"></exception>
            public void OnStaticIndexedHeader(int index, ReadOnlySpan<byte> value)
            {

            }


            public void OnHeader(ReadOnlySpan<byte> name, ReadOnlySpan<byte> value)
            {

            }

            public void OnHeadersComplete(bool endStream)
            {

            }
        }
    }
}
