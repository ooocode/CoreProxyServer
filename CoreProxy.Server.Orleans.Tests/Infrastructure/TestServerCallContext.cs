using Grpc.Core;
using Microsoft.AspNetCore.Http;
using System.Net;

namespace CoreProxy.Server.Orleans.Tests.Infrastructure
{
    /// <summary>
    /// 手写的 ServerCallContext 实现。
    /// MyGrpcService.CheckPassword 会调用 Grpc.AspNetCore.Server 的 GetHttpContext() 扩展方法，
    /// 它从 UserState["__HttpContext"] 取 HttpContext，所以这里必须塞入一个带 RemoteIpAddress 的假 HttpContext。
    /// </summary>
    public sealed class TestServerCallContext : ServerCallContext
    {
        private readonly Metadata requestHeaders;
        private readonly CancellationToken cancellationToken;

        public TestServerCallContext(Metadata requestHeaders, CancellationToken cancellationToken, IPAddress? remoteIpAddress)
        {
            this.requestHeaders = requestHeaders;
            this.cancellationToken = cancellationToken;

            var httpContext = new DefaultHttpContext();
            httpContext.Connection.RemoteIpAddress = remoteIpAddress;
            UserState[HttpContextKey] = httpContext;
        }

        /// <summary>Grpc.AspNetCore.Server.ServerCallContextExtensions 内部使用的键</summary>
        public const string HttpContextKey = "__HttpContext";

        protected override string MethodCore => "/hello.Greeter/StreamHandler";

        protected override string HostCore => "localhost";

        protected override string PeerCore => "ipv4:127.0.0.1:54321";

        protected override DateTime DeadlineCore => DateTime.MaxValue;

        protected override Metadata RequestHeadersCore => requestHeaders;

        protected override CancellationToken CancellationTokenCore => cancellationToken;

        protected override Metadata ResponseTrailersCore { get; } = [];

        protected override Status StatusCore { get; set; }

        protected override WriteOptions? WriteOptionsCore { get; set; }

        protected override AuthContext AuthContextCore { get; } = new TestAuthContext();

        protected override Task WriteResponseHeadersAsyncCore(Metadata responseHeaders) => Task.CompletedTask;

        protected override ContextPropagationToken CreatePropagationTokenCore(ContextPropagationOptions? options) =>
            throw new NotSupportedException();

        /// <summary>AuthContext 的构造函数要求传入认证属性表，StreamHandler 用不到，给空表即可</summary>
        private sealed class TestAuthContext : AuthContext
        {
            public TestAuthContext() : base(string.Empty, new Dictionary<string, List<AuthProperty>>())
            {
            }
        }
    }
}
