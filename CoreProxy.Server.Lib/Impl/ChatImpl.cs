namespace CoreProxy.Server.Lib.Impl
{
    public class ChatImpl;
}
//using Google.Protobuf.WellKnownTypes;
//using Grpc.Core;
//using Hello;
//using System.Collections.Concurrent;
//using System.Threading.Channels;

//namespace ServerWebApplication.Impl
//{
//    public class ChatImpl(ILogger<ChatImpl> logger) : ChatGrpc.ChatGrpcBase
//    {
//        private readonly ILogger<ChatImpl> logger = logger;
//        private static readonly ConcurrentDictionary<string, Channel<ExchangeMessagesResponse>> ResponseClients = new();

//        public override async Task ExchangeMessages(IAsyncStreamReader<ExchangeMessagesRequest> requestStream,
//            IServerStreamWriter<ExchangeMessagesResponse> responseStream, ServerCallContext context)
//        {
//            var myUserId = context.RequestHeaders.GetValue("UserId");
//            if (string.IsNullOrWhiteSpace(myUserId))
//            {
//                throw new RpcException(new Status(StatusCode.InvalidArgument, "userId不存在"));
//            }

//            if (!ResponseClients.TryAdd(myUserId, Channel.CreateUnbounded<ExchangeMessagesResponse>()))
//            {
//                throw new RpcException(new Status(StatusCode.AlreadyExists, "重复上线:" + myUserId));
//            }

//            try
//            {
//                logger.LogInformation("上线：" + myUserId);

//                var cancelToken = context.CancellationToken;

//                var task1 = Task.Run(async () =>
//                {
//                    //接收客户端请求，放到发送方的队列中
//                    await foreach (var item in requestStream.ReadAllAsync(cancelToken))
//                    {
//                        if (ResponseClients.TryGetValue(item.ToUserId, out var toQueue))
//                        {
//                            await toQueue.Writer.WriteAsync(new ExchangeMessagesResponse
//                            {
//                                FromUserId = myUserId,
//                                Message = item.Message,
//                                SendTime = item.SendTime
//                            }, cancelToken);
//                        }
//                    }
//                });

//                var task2 = Task.Run(async () =>
//                {
//                    if (ResponseClients.TryGetValue(myUserId, out var myQueue))
//                    {
//                        await foreach (var item in myQueue.Reader.ReadAllAsync(cancelToken))
//                        {
//                            await responseStream.WriteAsync(item, cancelToken);
//                        }
//                    }
//                });

//                await Task.WhenAll(task1, task2);
//            }
//            catch (Exception ex)
//            {
//                logger.LogError($"{myUserId} 离线 {ex.Message}");
//            }
//            finally
//            {
//                if (ResponseClients.TryRemove(myUserId, out var myQueue))
//                {
//                    myQueue.Writer.Complete();
//                }
//            }
//        }

//        public override Task<GetAllClientsResponse> GetAllClients(Empty request, ServerCallContext context)
//        {
//            GetAllClientsResponse response = new();
//            response.UserIds.AddRange(ResponseClients.Select(e => e.Key));
//            return Task.FromResult(response);
//        }
//    }
//}