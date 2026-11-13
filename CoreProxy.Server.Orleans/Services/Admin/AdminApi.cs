namespace CoreProxy.Server.Orleans.Services
{
    /// <summary>
    /// 管理页（/admin 静态文件 + /api/status 数据）的服务端入口。
    /// 鉴权用 HTTP Basic 复用 gRPC 的同一把口令；与 TunnelGrpcService.CheckPassword 不同的是，
    /// 这里**不做回环豁免** —— 本机访问同样要求凭据。
    /// 具体实现按职责拆在同目录的 partial 文件中。
    /// </summary>
    public static partial class AdminApi
    {
        private const string BasicPrefix = "Basic ";

        /// <summary>未指定 timeoutSeconds 时的默认等待上限（15 分钟）。</summary>
        public const int DefaultInvokeTimeoutSeconds = 900;

        /// <summary>
        /// 等客户端返回值的默认上限。SignalR 的 InvokeAsync 本身不设超时，
        /// 客户端既不返回也不断开时会一直挂着，故由服务端这边兜底；
        /// 单次请求可用 InvokeRequest.TimeoutSeconds 覆盖。
        /// </summary>
        private static readonly TimeSpan DefaultInvokeTimeout = TimeSpan.FromSeconds(DefaultInvokeTimeoutSeconds);

        /// <summary>
        /// CancellationTokenSource.CancelAfter 能接受的最大延迟（uint.MaxValue - 1 毫秒，约 49.7 天），
        /// 超过它会抛 ArgumentOutOfRangeException。timeoutSeconds 本身不设业务上限，
        /// 但计时器必须截到这个平台可表达的范围内；对任何现实等待时长而言两者等价。
        /// </summary>
        private static readonly TimeSpan MaxTimerDelay = TimeSpan.FromMilliseconds(uint.MaxValue - 1);
    }
}
