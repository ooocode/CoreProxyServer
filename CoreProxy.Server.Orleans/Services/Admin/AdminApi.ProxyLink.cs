using Microsoft.AspNetCore.Http;

namespace CoreProxy.Server.Orleans.Services
{
    public static partial class AdminApi
    {
        /// <summary>
        /// 处理 POST /api/proxy-link：把目标地址加密成反代 token（鉴权同上，未授权 401）。
        /// 只接受 http/https 绝对地址，非法输入返回 400。
        /// 返回的 path 形如 <c>/{token}/</c>，调用方拼上自己的 origin 即为可用地址；
        /// 该 token 只有服务端密钥能签发，客户端无法自行构造其它目标。
        /// </summary>
        public static IResult HandleProxyLink(ProxyLinkRequest request, string password, HttpContext ctx)
        {
            if (RejectUnauthorized(ctx, password) is { } rejection)
            {
                return rejection;
            }

            if (request is null || !TryNormalizeTarget(request.Url, out var target))
            {
                return Results.StatusCode(StatusCodes.Status400BadRequest);
            }

            var token = ProxyTokenProtector.Encrypt(target);
            return Results.Json(
                new ProxyLinkPayload(token, "/" + token + "/"),
                AdminJsonContext.Default.ProxyLinkPayload);
        }

        /// <summary>
        /// 规范化代理目标：只接受 http/https 绝对地址，返回 <c>scheme://host[:port]</c>。
        /// 带路径的输入只保留 authority（路径由使用方在 token 段之后追加）。
        /// </summary>
        private static bool TryNormalizeTarget(string? url, out string target)
        {
            target = string.Empty;
            if (string.IsNullOrWhiteSpace(url) || !Uri.TryCreate(url.Trim(), UriKind.Absolute, out var uri))
            {
                return false;
            }

            if (uri.Scheme != Uri.UriSchemeHttp && uri.Scheme != Uri.UriSchemeHttps)
            {
                return false;
            }

            if (string.IsNullOrEmpty(uri.Host))
            {
                return false;
            }

            target = uri.GetLeftPart(UriPartial.Authority);
            return true;
        }
    }
}
