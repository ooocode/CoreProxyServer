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
using System.Diagnostics;
using System.Net;
using System.Runtime.InteropServices;
using System.Text;

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
        SignalRTransportHandlerService signalRTransportHandlerService,
        ILogger<Socket5ConnectionHandlerService> logger)
    {
        private static readonly byte[] Bytes50 = [5, 0];

        public async Task OnConnectedAsync(ConnectionContext connection, ConnectionDelegate next)
        {
            using var cancellationSource = CancellationTokenSource.CreateLinkedTokenSource(
              connection.ConnectionClosed, hostApplicationLifetime.ApplicationStopping);
            var cancellationToken = cancellationSource.Token;

            var firstResult = await connection.Transport.Input.ReadAsync(cancellationToken);
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
            //     //if (ipEndPoint.Port == 1079 && enableSignalR)
            //     //{
            //await signalRTransportHandlerService.DualStreamHandlerAsync(serverItem,
            //    connection,
            //    socket5Result.Address, socket5Result.Port, ProxyType.Socket5, tokenSource.Token);
            // }
            //else
            var processGrpcClient = grpcClientFactory.CreateClient<Greeter.GreeterClient>(serverItem.LocalListenerPort.ToString());
            await grpcTransportHandlerService.DualStreamHandlerAsync(processGrpcClient, connection.Transport,
                socket5Result.Uri, ProxyType.Socket5, tokenSource);
        }


        private static bool IsHttp(ReadOnlySequence<byte> buffer)
        {
            try
            {
                var reader = new SequenceReader<byte>(buffer);
                var handler = new ProbeHandler();
                var parser = new HttpParser<ProbeHandler>();

                //http
                parser.ParseRequestLine(handler, ref reader);
                return true;
            }
            catch (BadHttpRequestException ex)
            {
                //自定义处理...
                if (ex.Message == "Detected a TLS handshake to an endpoint that does not have TLS enabled.")
                {
                    //https
                    return true;
                }
            }
            return false;
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
