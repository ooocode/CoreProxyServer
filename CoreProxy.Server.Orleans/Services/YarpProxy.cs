using System.Net;
using System.Net.Security;
using System.Security.Cryptography.X509Certificates;
using System.Text;
using Yarp.ReverseProxy.Forwarder;

namespace CoreProxy.Server.Orleans.Services
{
    /// <summary>
    /// YARP 反向代理接入。
    /// 请求路径以 <c>/{hex}(@host@)/...</c> 开头（或带 proxy Cookie）时，整条请求原样转发到解析出的 host。
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
        /// 判定与转发写在同一段中间件里（而不是 MapWhen 分支）：命中与否只解析一次 host，
        /// 未命中的普通请求只多一次首段扫描，直接放行给下游中间件。
        /// </summary>
        public static WebApplication UseYarpReverseProxy(this WebApplication app)
        {
            var httpForwarder = app.Services.GetRequiredService<IHttpForwarder>();

            app.Use(async (context, next) =>
            {
                var path = context.Request.Path.ToUriComponent();

                // 优先取路径首段；不是合法 hex(@host@) 时退回 proxy Cookie
                var host = GetHost(GetFirstSegment(path));
                if (host.Length == 0)
                {
                    host = GetHost(context.Request.Cookies["proxy"]);
                }

                if (host.Length == 0)
                {
                    await next(context);
                    return;
                }

                var hostHex = Convert.ToHexStringLower(Encoding.UTF8.GetBytes($"@{host}@"));

                // 去掉路径里的 host 段，剩余部分原样转给目标
                var parts = path.Split('/', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
                context.Request.Path = '/' + string.Join('/', parts.Where(x => x != hostHex));

                // 反向代理要把 Host 改成目标自身的 authority：目标应用常据此生成绝对 URL
                // （重定向、静态资源、回跳地址）。转发原始 Host 会让它回写指向代理本机地址的跳转，
                // 实测目标把 Location 生成成了 https://127.0.0.1:5000/...。
                var targetAuthority = Uri.TryCreate(host, UriKind.Absolute, out var targetUri)
                    ? targetUri.Authority
                    : host;

                CookieTransformer cookieTransformer = new CookieTransformer(hostHex, targetAuthority);
                await httpForwarder.SendAsync(
                    context,
                    host, // 目标地址
                    ForwarderInvoker,
                    requestConfig: ForwarderRequestConfig.Empty,
                    transformer: cookieTransformer
                );
            });

            return app;
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

        /// <summary>
        /// 解析 hex 编码的 <c>@host@</c>；不合法时返回空串（调用方以此判定"不是代理请求"）。
        /// 先逐字符校验 hex 再解码：普通请求（/api/status 之类）不该靠抛 FormatException 来做控制流。
        /// </summary>
        private static string GetHost(ReadOnlySpan<char> input)
        {
            if (input.Length < 2 || (input.Length & 1) != 0)
            {
                return string.Empty;
            }

            foreach (var c in input)
            {
                if (!char.IsAsciiHexDigit(c))
                {
                    return string.Empty;
                }
            }

            var raw = Encoding.UTF8.GetString(Convert.FromHexString(input));
            if (raw.StartsWith('@') && raw.EndsWith('@'))
            {
                return raw.Replace("@", string.Empty);
            }

            return string.Empty;
        }
    }

    public class CookieTransformer(string targetHost, string targetAuthority) : HttpTransformer
    {
        /// <summary>
        /// 把请求 Host 覆盖成目标 authority，保证目标应用生成的绝对 URL 指向它自己而不是代理入口。
        /// </summary>
        public override ValueTask TransformRequestAsync(HttpContext httpContext, HttpRequestMessage proxyRequest, string destinationPrefix, CancellationToken cancellationToken)
        {
            var result = base.TransformRequestAsync(httpContext, proxyRequest, destinationPrefix, cancellationToken);
            proxyRequest.Headers.Host = targetAuthority;
            return result;
        }

        public override async ValueTask<bool> TransformResponseAsync(HttpContext httpContext, HttpResponseMessage? proxyResponse, CancellationToken cancellationToken)
        {
            var result = await base.TransformResponseAsync(httpContext, proxyResponse, cancellationToken);

            // 只需 Append：同名同路径的 Set-Cookie 会覆盖旧值（包括目标自己回写的 proxy）。
            // 之前先 Delete 再 Append 会多发一条过期 Cookie，浏览器最终值相同，纯属冗余。
            httpContext.Response.Cookies.Append("proxy", targetHost);

            return result;
        }
    }
}
