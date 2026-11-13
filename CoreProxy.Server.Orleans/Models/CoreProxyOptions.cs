using Microsoft.Extensions.Configuration;

namespace CoreProxy.Server.Orleans.Models
{
    /// <summary>
    /// 服务端启动配置的类型化视图。手工解析 IConfiguration（不使用 ConfigurationBinder），
    /// 解析与容错规则与原先散落在 Program/TunnelGrpcService 里的 int.TryParse 判断逐条一致。
    /// </summary>
    public sealed record CoreProxyOptions(
        TimeSpan ConnectTimeout,
        TimeSpan DnsCacheTtl,
        TimeSpan AdminInvokeTimeout,
        int? Http2MaxStreamsPerConnection)
    {
        private const int DefaultConnectTimeoutSeconds = 20;
        private const int DefaultDnsCacheTtlSeconds = 60;
        private const int DefaultAdminInvokeTimeoutSeconds = 900;

        public static CoreProxyOptions FromConfiguration(IConfiguration configuration)
        {
            var connectTimeout =
                int.TryParse(configuration["ConnectTimeoutSeconds"], out var connectSeconds) && connectSeconds > 0
                    ? TimeSpan.FromSeconds(connectSeconds)
                    : TimeSpan.FromSeconds(DefaultConnectTimeoutSeconds);

            // 0 合法：表示禁用 DNS 缓存（每次建连都重新解析）。
            var dnsCacheTtl =
                int.TryParse(configuration["TargetDnsCacheTtlSeconds"], out var ttlSeconds) && ttlSeconds >= 0
                    ? TimeSpan.FromSeconds(ttlSeconds)
                    : TimeSpan.FromSeconds(DefaultDnsCacheTtlSeconds);

            var adminInvokeTimeout =
                int.TryParse(configuration["AdminInvokeTimeoutSeconds"], out var invokeSeconds) && invokeSeconds > 0
                    ? TimeSpan.FromSeconds(invokeSeconds)
                    : TimeSpan.FromSeconds(DefaultAdminInvokeTimeoutSeconds);

            int? http2MaxStreams =
                int.TryParse(configuration["Http2MaxStreamsPerConnection"], out var maxStreams) && maxStreams > 0
                    ? maxStreams
                    : null;

            return new CoreProxyOptions(connectTimeout, dnsCacheTtl, adminInvokeTimeout, http2MaxStreams);
        }
    }
}
