using Microsoft.VisualStudio.TestTools.UnitTesting;
using System.Net;
using System.Net.Sockets;

namespace CoreProxy.Server.Orleans.Tests.Infrastructure
{
    /// <summary>
    /// 用真实 Socket 扮演"目标服务器"，这样才能验证字节流完整性和半关闭（FIN）。
    /// </summary>
    public sealed class TestTargetServer : IDisposable
    {
        private readonly Socket listener;

        public TestTargetServer(IPAddress? address = null)
        {
            Address = address ?? IPAddress.Loopback;
            listener = new Socket(Address.AddressFamily, SocketType.Stream, ProtocolType.Tcp);
            listener.Bind(new IPEndPoint(Address, 0));
            listener.Listen(8);
            Port = ((IPEndPoint)listener.LocalEndPoint!).Port;
        }

        public IPAddress Address { get; }

        public int Port { get; }

        /// <summary>写入 X-Requested-With 的目标地址；IPv6 字面量需要方括号</summary>
        public string Uri => Address.AddressFamily == AddressFamily.InterNetworkV6
            ? $"http://[{Address}]:{Port}/"
            : $"http://{Address}:{Port}/";

        public Task<Socket> AcceptAsync(TimeSpan timeout) => listener.AcceptAsync().WaitAsync(timeout);

        public void Dispose() => listener.Dispose();

        /// <summary>读满指定字节数；提前收到 EOF 视为失败</summary>
        public static async Task<byte[]> ReadExactlyAsync(Socket socket, int count, TimeSpan timeout)
        {
            var buffer = new byte[count];
            var offset = 0;
            while (offset < count)
            {
                var read = await socket.ReceiveAsync(buffer.AsMemory(offset), SocketFlags.None).AsTask().WaitAsync(timeout);
                if (read == 0)
                {
                    Assert.Fail($"目标服务器只收到 {offset}/{count} 字节就收到了 EOF");
                }

                offset += read;
            }

            return buffer;
        }

        /// <summary>一直读到 EOF（对端 FIN），返回累计字节。超时说明对端没有发 FIN</summary>
        public static async Task<byte[]> ReadToEndAsync(Socket socket, TimeSpan timeout)
        {
            var buffer = new byte[16 * 1024];
            using var stream = new MemoryStream();
            while (true)
            {
                var read = await socket.ReceiveAsync(buffer.AsMemory(), SocketFlags.None).AsTask().WaitAsync(timeout);
                if (read == 0)
                {
                    return stream.ToArray();
                }

                stream.Write(buffer, 0, read);
            }
        }

        public static async Task WriteExactlyAsync(Socket socket, ReadOnlyMemory<byte> data, TimeSpan timeout)
        {
            var offset = 0;
            while (offset < data.Length)
            {
                var sent = await socket.SendAsync(data[offset..], SocketFlags.None).AsTask().WaitAsync(timeout);
                offset += sent;
            }
        }
    }
}
