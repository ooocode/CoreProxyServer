using Hello;
using System.Threading.Channels;

namespace CoreProxy.Server.Orleans.Services
{
    public class SessionInfo
    {
        /// <summary>
        /// 发起人
        /// </summary>
        public required string Creator { get; set; }

        public required Channel<ReadOnlyMemory<byte>> ChannelA { get; set; }

        public required Channel<ReadOnlyMemory<byte>> ChannelB { get; set; }
    }

    public static class GloableSessionsManager
    {
        public static readonly System.Collections.Concurrent.ConcurrentDictionary<string, SessionInfo> SessionList = [];

        /// <summary>
        /// signalr在线客户端 [deviceId-SignalrOnlineClient]
        /// </summary>
        public static readonly System.Collections.Concurrent.ConcurrentDictionary<string, SignalrOnlineClient> SignalrOnlineClients = [];
    }
}
