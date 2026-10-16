using CoreProxy.Server.Orleans.Internal;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace CoreProxy.Server.Orleans.Services
{
    /// <summary>/api/status 的响应体。camelCase 由 AdminJsonContext 的命名策略决定。</summary>
    public sealed record StatusPayload(int Total, IReadOnlyList<RuntimeTunnel> Items);

    /// <summary>
    /// 管理接口专用的 source-gen JSON 上下文（AOT）。
    /// 刻意与 AppJsonSerializerContext 分开：后者的 Options 被 SignalR 的 JSON 协议共用
    /// （Program.cs 里 AddJsonProtocol），改它的命名策略会改掉 SignalR 的线上格式。
    /// </summary>
    [JsonSourceGenerationOptions(PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase)]
    [JsonSerializable(typeof(StatusPayload))]
    public partial class AdminJsonContext : JsonSerializerContext;

    /// <summary>
    /// 管理页（/admin 静态文件 + /api/status 数据）的服务端入口。
    /// 鉴权用 HTTP Basic 复用 gRPC 的同一把口令；与 MyGrpcService.CheckPassword 不同的是，
    /// 这里**不做回环豁免** —— 本机访问同样要求凭据。
    /// </summary>
    public static class AdminApi
    {
        private const string BasicPrefix = "Basic ";

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

            byte[] raw;
            try
            {
                raw = Convert.FromBase64String(header[BasicPrefix.Length..].Trim());
            }
            catch (FormatException)
            {
                return false;
            }

            var decoded = Encoding.UTF8.GetString(raw);
            var separator = decoded.IndexOf(':');
            var candidate = separator >= 0 ? decoded[(separator + 1)..] : decoded;

            return CryptographicOperations.FixedTimeEquals(
                Encoding.UTF8.GetBytes(candidate),
                Encoding.UTF8.GetBytes(password));
        }

        /// <summary>
        /// 处理 GET /api/status：未带凭据返回 401 + WWW-Authenticate；否则返回当前 gRPC 隧道清单 JSON。
        /// </summary>
        public static Task HandleStatusAsync(HttpContext ctx, string password)
        {
            if (!CheckBasicAuth(ctx, password))
            {
                ctx.Response.StatusCode = 401;
                ctx.Response.Headers.WWWAuthenticate = "Basic realm=\"admin\"";
                return Task.CompletedTask;
            }

            var items = ConnectionRepository.GetGrpcTunnels();
            ctx.Response.StatusCode = 200;
            ctx.Response.ContentType = "application/json";
            return ctx.Response.WriteAsJsonAsync(
                new StatusPayload(items.Count, items),
                AdminJsonContext.Default.StatusPayload);
        }
    }
}
