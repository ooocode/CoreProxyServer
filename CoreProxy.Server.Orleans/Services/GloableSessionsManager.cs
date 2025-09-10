using System.Threading.Channels;

namespace CoreProxy.Server.Orleans.Services
{
    public class SessionInfo
    {
        /// <summary>
        /// 发起人
        /// </summary>
        public required string Creator { get; set; }

        /// <summary>
        /// 等待加入
        /// </summary>
        public required Channel<string> ChannelWait { get; set; }

        public required Channel<ReadOnlyMemory<byte>> ChannelA { get; set; }

        public required Channel<ReadOnlyMemory<byte>> ChannelB { get; set; }
    }

    public static class GloableSessionsManager
    {
        public static readonly System.Collections.Concurrent.ConcurrentDictionary<string, SessionInfo> SessionList = [];
    }
}
