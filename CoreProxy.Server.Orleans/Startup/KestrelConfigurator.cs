using Microsoft.AspNetCore.Server.Kestrel.Core;
using System.Security.Cryptography.X509Certificates;

namespace CoreProxy.Server.Orleans.Startup
{
    internal static class KestrelConfigurator
    {
        public static void Configure(KestrelServerOptions serverOptions, IConfiguration configuration,
            X509Certificate2 certificate, int? maxStreamsPerConnection)
        {
            // 保活探测：连接上连续 30 秒没有任何【入站】帧时，服务端向客户端发 PING。
            // 注意：重置该计时器的是任意入站帧（含 WINDOW_UPDATE 等），并非只有 PING ACK，
            // 所以真正会被探测到的只有"完全静默"的连接；单向持续收数据的连接永远不会被 PING。
            // 30s 短于常见 NAT / 负载均衡的 60s 空闲超时，足以维持空闲隧道不被中间设备回收。
            serverOptions.Limits.Http2.KeepAlivePingDelay = TimeSpan.FromSeconds(30);

            // PING 发出后等待入站帧的时限，超时即判定对端已死，先发 GOAWAY(INTERNAL_ERROR) 再 FIN。
            // 该值只需覆盖 RTT + 抖动，无需留到分钟级；Kestrel 默认值为 20s。
            // 原先的 60s 会让死连接在被回收前多滞留约 45 秒（连接 + 上游目标连接 + 会话状态都跟着滞留）。
            // 实测：计时器由 1 秒心跳驱动，故对端完全静默到被回收的实际耗时 ≈ Delay + Timeout + 0~2s。
            serverOptions.Limits.Http2.KeepAlivePingTimeout = TimeSpan.FromSeconds(20);

            if (maxStreamsPerConnection is int maxStreams && maxStreams > 0)
            {
                serverOptions.Limits.Http2.MaxStreamsPerConnection = maxStreams;
            }

            serverOptions.AddServerHeader = false; // 禁用 Server 头

            // 按监听 URL 的 scheme 选择端点协议：
            // - HTTPS 端点：HTTP/1.1 + HTTP/2，由 ALPN 正常协商（浏览器与现代客户端走 h2，
            //   只支持 HTTP/1.1 的客户端回退 h1.1）。
            // - 明文端点：HTTP/2 prior knowledge（h2c），供 gRPC / tools/bench 使用。
            // ListenOptions 不暴露 TLS 状态，故按 --urls / Urls 的 scheme 判定；同一进程混用两种
            // scheme 时按明文处理（h2c），保证 gRPC 客户端不被破坏。
            string[] configuredUrls = (configuration["urls"] ?? "https://*:6099")
                .Split(';', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
            bool hasPlaintextEndpoint = configuredUrls.Any(url => url.StartsWith("http://", StringComparison.OrdinalIgnoreCase));
            serverOptions.ConfigureEndpointDefaults(c =>
                c.Protocols = hasPlaintextEndpoint ? HttpProtocols.Http2 : HttpProtocols.Http1AndHttp2);

#if !DEBUG
            serverOptions.ConfigureHttpsDefaults(s => s.ServerCertificate = certificate);
#endif

            //// --- HTTP/2 流量控制窗口 (核心中的核心) ---
            //// ConnectionWindow 必须略大于所有 StreamWindow 的总和
            //// 设置为 64MB 以完全覆盖 25MB 的 BDP，留出缓冲余量
            serverOptions.Limits.Http2.InitialConnectionWindowSize = 1024 * 1024 * 64;

            //// 单个 Stream 的窗口设置为 32MB，确保单个双向流就能跑满带宽
            serverOptions.Limits.Http2.InitialStreamWindowSize = 1024 * 1024 * 32;

            //// --- 缓冲区与帧大小 ---
            //// 增加最大帧大小以减少 Header 处理频率 (Max: 64KB - 1)
            serverOptions.Limits.Http2.MaxFrameSize = 16777215;

            //// 响应缓冲区：为了跑满 1Gbps，Kestrel 需要足够的内存来暂存即将发往网卡的数据
            //// 设置为 4MB 可以在 0GC 和 吞吐量之间取得平衡
            serverOptions.Limits.MaxResponseBufferSize = 1024 * 1024 * 4;

            //// 请求缓冲区：同样设置为 4MB
            serverOptions.Limits.MaxRequestBufferSize = 1024 * 1024 * 4;

            serverOptions.Limits.MinRequestBodyDataRate = null; // 高延迟下建议关闭或调低，防止误判为慢速攻击
            serverOptions.Limits.MinResponseDataRate = null;
        }
    }
}
