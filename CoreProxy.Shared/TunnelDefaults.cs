namespace CoreProxy.Shared
{
    /// <summary>
    /// 客户端与服务端共用的隧道协议常量。放在共享项目里，
    /// 避免两侧各写一份魔法值、改动时只改一边。
    /// </summary>
    public static class TunnelDefaults
    {
        /// <summary>一端已无数据可送时，等待另一端收尾的时间窗口。</summary>
        public static readonly TimeSpan TeardownWindow = TimeSpan.FromSeconds(5);

        /// <summary>gRPC 隧道请求头：目标地址（scheme://host:port 由客户端拼接）。</summary>
        public const string RequestedWithHeader = "X-Requested-With";
    }
}
