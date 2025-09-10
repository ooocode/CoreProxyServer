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

        public required Channel<byte[]> ChannelA { get; set; }

        public required Channel<byte[]> ChannelB { get; set; }
    }

    public static class GloableSessionsManager
    {
        public static readonly System.Collections.Concurrent.ConcurrentDictionary<string, SessionInfo> SessionList = [];

        ///// <summary>
        ///// 创建并等待别的客户端加入
        ///// </summary>
        ///// <param name="current"></param>
        ///// <param name="cancellationToken"></param>
        ///// <returns></returns>
        ///// <exception cref="Exception"></exception>
        //public static async Task CreateAndWaitAsync(string current, CancellationToken cancellationToken)
        //{
        //    SessionInfo sessionInfo = new()
        //    {
        //        ChannelA = Channel.CreateUnbounded<byte[]>(),
        //        ChannelB = Channel.CreateUnbounded<byte[]>(),
        //        ChannelWait = Channel.CreateBounded<string>(1)
        //    };

        //    if (!SessionList.TryAdd(current, sessionInfo))
        //    {
        //        throw new Exception("重复创建session");
        //    }

        //    var target = await sessionInfo.ChannelWait.Reader.ReadAsync(cancellationToken);
        //    Console.WriteLine($"{current}<-->{target}");
        //}

        ///// <summary>
        ///// 加入会话
        ///// </summary>
        ///// <param name="current"></param>
        ///// <param name="target"></param>
        ///// <exception cref="Exception"></exception>
        //public static void Join(string current, string target)
        //{
        //    if (!SessionList.TryGetValue(target, out var session))
        //    {
        //        throw new Exception($"不存在目标session {target}");
        //    }

        //    if (!session.ChannelWait.Writer.TryWrite(current))
        //    {
        //        throw new Exception("目标session已经被别的客户端连接了");
        //    }
        //}
    }
}
