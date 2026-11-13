using Microsoft.AspNetCore.Http;
using System.Security.Cryptography;
using System.Text;

namespace CoreProxy.Server.Orleans.Services
{
    public static partial class AdminApi
    {
        /// <summary>
        /// 判定某请求路径是否属于受保护的管理面。
        /// 注意 /admin 与 /admin.html 是两个不同的路径段：StartsWithSegments("/admin") 匹配不到
        /// /admin.html，漏掉它会让静态页面绕过鉴权被直接访问，故两个前缀都要显式列出。
        /// API 路径同样用 StartsWithSegments：既容忍尾斜杠，又不会误匹配 /api/statusx 这类同前缀路径。
        /// </summary>
        public static bool RequiresAuth(PathString path) =>
            path.StartsWithSegments("/admin")
            || path.StartsWithSegments("/admin.html")
            || path.StartsWithSegments("/api/status")
            || path.StartsWithSegments("/api/devices")
            // /api/invoke 有意匿名开放：外部设备/脚本需要直接调用，鉴权由调用方网络边界负责。
            || path.StartsWithSegments("/api/proxy-link");

        /// <summary>
        /// 校验 HTTP Basic 凭据。口令取解码后 ":" 之后那一段（无 ":" 则取整段），
        /// 用常数时间比较避免时序泄漏。缺头、scheme 不符、base64 非法一律返回 false 且不抛异常。
        /// </summary>
        public static bool CheckBasicAuth(HttpContext ctx, string password)
        {
            var header = ctx.Request.Headers.Authorization.ToString();
            if (!header.StartsWith(BasicPrefix, StringComparison.Ordinal))
            {
                return false;
            }

            var encoded = header[BasicPrefix.Length..].Trim();

            // 非法 base64 是外部可直接构造的输入：用 TryFromBase64String 而不是
            // FromBase64String + catch，避免每次探测都抛/接一个 FormatException。
            // 两者对空串、内嵌空白与非法字符的接受/拒绝语义一致。
            var raw = new byte[encoded.Length];
            if (!Convert.TryFromBase64String(encoded, raw, out var written))
            {
                return false;
            }

            var decoded = Encoding.UTF8.GetString(raw, 0, written);
            var separator = decoded.IndexOf(':');
            var candidate = separator >= 0 ? decoded[(separator + 1)..] : decoded;

            // 先各自哈希成定长再比较：FixedTimeEquals 对长度不同的输入会立即返回，
            // 直接比原文会泄漏口令长度。
            return CryptographicOperations.FixedTimeEquals(
                SHA256.HashData(Encoding.UTF8.GetBytes(candidate)),
                SHA256.HashData(Encoding.UTF8.GetBytes(password)));
        }

        /// <summary>统一的 401 响应：状态码 + Basic 挑战头。中间件与各 handler 共用，避免两处漂移。</summary>
        public static void WriteUnauthorized(HttpContext ctx)
        {
            ctx.Response.StatusCode = StatusCodes.Status401Unauthorized;
            ctx.Response.Headers.WWWAuthenticate = "Basic realm=\"admin\"";
        }

        /// <summary>鉴权通过返回 null；否则写好 401 挑战并返回一个空 IResult 供调用方直接返回。</summary>
        private static IResult? RejectUnauthorized(HttpContext ctx, string password)
        {
            if (CheckBasicAuth(ctx, password))
            {
                return null;
            }

            WriteUnauthorized(ctx);
            return Results.Empty;
        }
    }
}
