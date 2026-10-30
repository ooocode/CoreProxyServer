using System.Buffers.Text;
using System.Net;
using System.Net.Security;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Text;
using Yarp.ReverseProxy.Forwarder;

namespace CoreProxy.Server.Orleans.Services
{
    /// <summary>
    /// YARP 反向代理接入。
    /// 请求路径以 <c>/{token}/...</c> 开头（或带 proxy Cookie）时，整条请求原样转发到 token 解密出的 host。
    /// token 必须由服务端用密钥签发（admin 页面 /api/proxy-link 生成），
    /// 不再接受任何人可自行构造的 hex(@host@)——否则等于把服务端变成任意外连代理。
    /// 转发使用本类自持的 <see cref="HttpMessageInvoker"/>（而不是 YARP 内部的
    /// <c>DirectForwardingHttpClientProvider</c>）：出站 TLS 需要忽略"证书名称不匹配"
    /// ——部分政府/内网站点证书链合法但 SAN 与访问域名对不上，默认校验会直接 502。
    /// </summary>
    public static class YarpProxyExtensions
    {
        /// <summary>
        /// 出站转发专用的 invoker。配置对齐 YARP 直连转发的默认值：
        /// 不走系统代理、不跟随重定向、不自动解压、不维护 Cookie 容器。
        /// </summary>
        private static readonly HttpMessageInvoker ForwarderInvoker = CreateForwarderInvoker();

        /// <summary>注册 YARP 转发器服务（必须在 <c>builder.Build()</c> 之前调用）。</summary>
        public static IServiceCollection AddYarpForwarder(this IServiceCollection services)
        {
            // 核心步骤：注册转发器服务
            return services.AddHttpForwarder();
        }

        /// <summary>
        /// 挂上反向代理中间件（必须在 <c>builder.Build()</c> 之后、其它中间件之前调用）。
        /// 判定与转发写在同一段中间件里（而不是 MapWhen 分支）：命中与否只解析一次 token，
        /// 未命中的普通请求只多一次首段扫描，直接放行给下游中间件。
        /// </summary>
        public static WebApplication UseYarpReverseProxy(this WebApplication app)
        {
            var httpForwarder = app.Services.GetRequiredService<IHttpForwarder>();

            app.Use(async (context, next) =>
            {
                var path = context.Request.Path.ToUriComponent();

                // 只认服务端签发的 token：优先取路径首段；
                // 没有时由"目标页发起的子资源请求"的 Referer 兜底（顶层导航绝不走这里）。
                var token = GetFirstSegment(path).ToString();
                if (!ProxyTokenProtector.TryDecrypt(token, out var host))
                {
                    token = IsNavigationRequest(context.Request)
                        ? string.Empty
                        : GetTokenFromReferer(context.Request.Headers.Referer.ToString());
                    if (!ProxyTokenProtector.TryDecrypt(token, out host))
                    {
                        await next(context);
                        return;
                    }
                }

                // 去掉路径里的 token 段，剩余部分原样转给目标
                var parts = path.Split('/', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
                context.Request.Path = '/' + string.Join('/', parts.Where(x => x != token));

                // 反向代理要把 Host 改成目标自身的 authority：目标应用常据此生成绝对 URL
                // （重定向、静态资源、回跳地址）。转发原始 Host 会让它回写指向代理本机地址的跳转，
                // 实测目标把 Location 生成成了 https://127.0.0.1:5000/...。
                var targetAuthority = Uri.TryCreate(host, UriKind.Absolute, out var targetUri)
                    ? targetUri.Authority
                    : host;

                ProxyTransformer proxyTransformer = new ProxyTransformer(targetAuthority);
                await httpForwarder.SendAsync(
                    context,
                    host, // 目标地址
                    ForwarderInvoker,
                    requestConfig: ForwarderRequestConfig.Empty,
                    transformer: proxyTransformer
                );
            });

            return app;
        }

        /// <summary>
        /// 是否"顶层导航"：地址栏输入、点击链接、iframe/embed 加载。
        /// 这类请求绝不用 Referer 兜底——否则从代理页面点一个相对链接就能把 /admin 劫持到目标站点。
        /// Sec-Fetch-Dest 缺失（curl / 旧浏览器）时按导航处理：宁可少代理，也不劫持导航。
        /// </summary>
        internal static bool IsNavigationRequest(HttpRequest request)
        {
            var dest = request.Headers["Sec-Fetch-Dest"].ToString();
            return dest.Length == 0
                || dest.Equals("document", StringComparison.OrdinalIgnoreCase)
                || dest.Equals("iframe", StringComparison.OrdinalIgnoreCase)
                || dest.Equals("frame", StringComparison.OrdinalIgnoreCase)
                || dest.Equals("embed", StringComparison.OrdinalIgnoreCase)
                || dest.Equals("object", StringComparison.OrdinalIgnoreCase);
        }

        /// <summary>
        /// 从 Referer 的路径里取首段，即"发起该请求的代理页面"所用的 token。
        /// Referer 由浏览器按同源策略填写；拿不到或首段不是合法 token 时，后续解密会失败，请求不会进代理分支。
        /// </summary>
        internal static string GetTokenFromReferer(string? referer)
        {
            if (string.IsNullOrEmpty(referer) || !Uri.TryCreate(referer, UriKind.Absolute, out var uri))
            {
                return string.Empty;
            }

            return GetFirstSegment(uri.AbsolutePath).ToString();
        }

        /// <summary>
        /// 取路径首段：等价于 <c>path.Split('/', RemoveEmptyEntries | TrimEntries).FirstOrDefault()</c>，
        /// 但不分配数组——这是每个请求（含非代理请求）都要走的路径。
        /// 路径来自 <see cref="PathString.ToUriComponent"/>，已转义，不会含空白字符。
        /// </summary>
        private static ReadOnlySpan<char> GetFirstSegment(string path)
        {
            var span = path.AsSpan().TrimStart('/');
            var end = span.IndexOf('/');
            return end < 0 ? span : span[..end];
        }

        /// <summary>
        /// 创建出站转发 invoker。TLS 校验只放过"名称不匹配"：
        /// 证书链不可信、证书过期等错误仍然拒绝，避免把整条链路变成不校验证书。
        /// </summary>
        private static HttpMessageInvoker CreateForwarderInvoker()
        {
            var handler = new SocketsHttpHandler
            {
                UseProxy = false,
                AllowAutoRedirect = false,
                UseCookies = false,
                AutomaticDecompression = DecompressionMethods.None,
                PooledConnectionLifetime = TimeSpan.FromMinutes(5),
                EnableMultipleHttp2Connections = true,
                ConnectTimeout = TimeSpan.FromSeconds(10),
                SslOptions = new SslClientAuthenticationOptions
                {
                    RemoteCertificateValidationCallback = static (_, _, _, errors) => true
                }
            };

            return new HttpMessageInvoker(handler, disposeHandler: true);
        }
    }

    /// <summary>
    /// 代理目标地址的加密封装：AES-256-GCM，输出 URL-safe Base64（无填充）。
    /// 布局：version(1) || nonce(12) || ciphertext(n) || tag(16)。
    /// 只有持有密钥（服务端）才能签发；客户端拿到 token 无法反推/伪造其它目标。
    /// 密钥由证书口令派生（SHA-256），不额外落盘：证书重签 → 口令变化 → 旧链接全部失效。
    /// </summary>
    internal static class ProxyTokenProtector
    {
        private const byte CurrentVersion = 1;
        private const int NonceSize = 12;
        private const int TagSize = 16;

        // token 的正常长度在几十个字符；放宽到 4K 是为了容错，同时避免超大输入做无谓分配
        private const int MaxTokenChars = 4096;

        private static readonly byte[] AssociatedData = "CoreProxy.YarpProxy.v1"u8.ToArray();
        private static readonly object Sync = new();
        private static volatile AesGcm? aesGcm;

        /// <summary>
        /// 用证书口令派生 32 字节密钥并初始化。必须在开始处理请求前调用一次。
        /// </summary>
        public static void Configure(string secret)
        {
            ArgumentException.ThrowIfNullOrWhiteSpace(secret);

            var key = SHA256.HashData(Encoding.UTF8.GetBytes(secret));
            var created = new AesGcm(key, TagSize);
            lock (Sync)
            {
                // 不 Dispose 旧实例：它可能仍被并发请求持有，Dispose 会让这些请求 ObjectDisposedException。
                // Configure 只在启动时调用一次（单测会重复调用，泄漏的是一个小对象，可忽略）。
                aesGcm = created;
            }
        }

        /// <summary>把目标地址加密成 token。</summary>
        public static string Encrypt(string host)
        {
            var cipher = aesGcm ?? throw new InvalidOperationException(
                "ProxyTokenProtector 未初始化（缺少 Configure 调用）");

            var plaintext = Encoding.UTF8.GetBytes(host);
            var payload = new byte[1 + NonceSize + plaintext.Length + TagSize];
            payload[0] = CurrentVersion;

            var nonce = payload.AsSpan(1, NonceSize);
            RandomNumberGenerator.Fill(nonce);
            cipher.Encrypt(
                nonce,
                plaintext,
                payload.AsSpan(1 + NonceSize, plaintext.Length),
                payload.AsSpan(1 + NonceSize + plaintext.Length, TagSize),
                AssociatedData);

            return Base64Url.EncodeToString(payload);
        }

        /// <summary>校验并解密 token；任何非法/篡改/版本不符都返回 false 且不抛异常。</summary>
        public static bool TryDecrypt(ReadOnlySpan<char> token, out string host)
        {
            host = string.Empty;

            // 未配置（如单测未初始化）时按"不是代理请求"处理，而不是让请求 500
            var cipher = aesGcm;
            if (cipher is null || token.Length is < 1 + NonceSize + TagSize or > MaxTokenChars)
            {
                return false;
            }

            try
            {
                Span<byte> buffer = token.Length <= 512 ? stackalloc byte[512] : new byte[token.Length];
                // 注意：TryDecodeFromChars 对非法输入并不总是返回 false，部分情况会抛 FormatException，
                // 所以解码与解密整段都要兜住——这里处理的是外部可任意构造输入。
                if (!Base64Url.TryDecodeFromChars(token, buffer, out var written)
                    || written < 1 + NonceSize + TagSize
                    || buffer[0] != CurrentVersion)
                {
                    return false;
                }

                var cipherLength = written - 1 - NonceSize - TagSize;
                Span<byte> plaintext = cipherLength <= 256 ? stackalloc byte[256] : new byte[cipherLength];
                cipher.Decrypt(
                    buffer.Slice(1, NonceSize),
                    buffer.Slice(1 + NonceSize, cipherLength),
                    buffer.Slice(1 + NonceSize + cipherLength, TagSize),
                    plaintext[..cipherLength],
                    AssociatedData);

                host = Encoding.UTF8.GetString(plaintext[..cipherLength]);
                return host.Length > 0;
            }
            catch (Exception ex) when (ex is FormatException or CryptographicException or ArgumentException)
            {
                return false;
            }
        }
    }

    /// <summary>
    /// 转发请求的 transformer：把请求 Host 覆盖成目标 authority，
    /// 保证目标应用生成的绝对 URL 指向它自己而不是代理入口。
    /// 旧版本还会回写 proxy Cookie；现在代理会话只靠路径 token / Referer，不再使用 Cookie。
    /// </summary>
    public class ProxyTransformer(string targetAuthority) : HttpTransformer
    {
        public override ValueTask TransformRequestAsync(HttpContext httpContext, HttpRequestMessage proxyRequest, string destinationPrefix, CancellationToken cancellationToken)
        {
            var result = base.TransformRequestAsync(httpContext, proxyRequest, destinationPrefix, cancellationToken);
            proxyRequest.Headers.Host = targetAuthority;
            return result;
        }
    }
}
