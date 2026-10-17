using Microsoft.AspNetCore.Connections;
using Microsoft.AspNetCore.Server.Kestrel.Transport.Sockets;
using Microsoft.Extensions.Options;

namespace CoreProxy.Server.Orleans.Internal
{
    /// <summary>
    /// 创建连接目标服务器所用的 IConnectionFactory。
    /// </summary>
    public static class TargetConnectionFactory
    {
        /// <summary>
        /// SocketConnectionFactory 是 internal，只能反射构造（与 Kestrel 内部注册路径一致）。
        /// </summary>
        private const string FactoryTypeName = "Microsoft.AspNetCore.Server.Kestrel.Transport.Sockets.SocketConnectionFactory";

        public static IConnectionFactory Create(SocketTransportOptions transportOptions, ILoggerFactory loggerFactory)
        {
            var factoryType = typeof(SocketTransportOptions).Assembly.GetType(FactoryTypeName);
            ArgumentNullException.ThrowIfNull(factoryType, nameof(factoryType));

            var options = new SocketTransportOptions
            {
                IOQueueCount = transportOptions.IOQueueCount,
                NoDelay = transportOptions.NoDelay,
                MaxReadBufferSize = transportOptions.MaxReadBufferSize,
                MaxWriteBufferSize = transportOptions.MaxWriteBufferSize,
                WaitForDataBeforeAllocatingBuffer = transportOptions.WaitForDataBeforeAllocatingBuffer,
            };

            return (IConnectionFactory)Activator.CreateInstance(factoryType, Options.Create(options), loggerFactory)!;
        }
    }
}
