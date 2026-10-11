using DotNext.Buffers;
using DotNext.Buffers.Binary;
using DotNext.IO.Pipelines;
using System.IO.Pipelines;
using System.Net;
using System.Runtime.InteropServices;
using System.Text;

namespace CoreProxy.Common
{
    [StructLayout(LayoutKind.Sequential, Pack = 1)]
    public struct Socket5Header
    {
        public byte Ver { get; init; }

        //SOCK的命令码，占用1个字节，表示建立连接的类型，其中，0x01表示CONNECT请求，0x02表示BIND请求，0x03表示UDP转发
        public byte Cmd { get; init; }

        //rsv 默认0x00  保留位 不使用
        public byte Rsv { get; init; }

        //DST.ADDR类型，
        //0x01表示IPv4地址，此时DST.ADDR部分4字节长度。
        //0x03则表示域名，此时DST.ADDR部分第一个字节为域名长度，DST.ADDR剩余的内容为域名，没有\0结尾。
        //0x04表示IPv6地址，此时DST.ADDR部分16个字节长度。
        public byte Atype { get; set; }
    }

    public class Socket5Result
    {
        public Socket5Header Header { get; set; }
        /// <summary>
        /// 目标地址
        /// </summary>
        public required string Address { get; set; }

        /// <summary>
        /// 端口
        /// </summary>
        public ushort Port { get; set; }

        public string Uri => $"tcp://{Address}:{Port}";

        public override string ToString()
        {
            return $"ver={Header.Ver} cmd={Header.Cmd} rsv={Header.Rsv} atype={Header.Atype} address={Address} port={Port}";
        }
    }

    /// <summary>
    /// socket5信息
    /// </summary>
    public static class Socket5Utility
    {
        /// <summary>
        /// 读取1个字节
        /// </summary>
        /// <param name="pipeReader"></param>
        /// <param name="cancellationToken"></param>
        /// <returns></returns>
        public static async Task<byte> Read1BytesAsync(PipeReader pipeReader, CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var result = await pipeReader.ReadAsync<Blittable<byte>>(cancellationToken);
            return result.Value;
        }

        /// <summary>
        /// 读取N个字节
        /// </summary>
        /// <param name="pipeReader"></param>
        /// <param name="length"></param>
        /// <param name="cancellationToken"></param>
        /// <returns></returns>
        public static async Task<MemoryOwner<byte>> ReadBytesAsync(PipeReader pipeReader, int length, CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var memoryOwner = MemoryAllocator<byte>.Default.AllocateExactly(length);
            await pipeReader.ReadExactlyAsync(memoryOwner.Memory, cancellationToken);
            return memoryOwner;
        }


        public static async Task<Socket5Result> Parse(PipeReader pipeReader, CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var socket5Header = await pipeReader.ReadAsync<Blittable<Socket5Header>>(cancellationToken);
            if (socket5Header.Value.Ver != 0x05)
            {
                throw new Exception("非socket5协议");
            }

            if (socket5Header.Value.Cmd != 0x01)
            {
                throw new Exception("非socket5协议");
            }

            if (socket5Header.Value.Rsv != 0x00)
            {
                throw new Exception("非socket5协议");
            }

            Socket5Result socket5Result = new()
            {
                Header = socket5Header.Value,
                Address = string.Empty,
            };

            if (socket5Header.Value.Atype == 0x03)
            {
                //域名
                var domainNameLength = await Read1BytesAsync(pipeReader, cancellationToken);
                if (domainNameLength == 0)
                {
                    //RFC 1928 规定域名长度至少为 1，长度为 0 说明不是合法请求
                    throw new Exception("域名长度不能为 0");
                }

                using var addressBytes = await ReadBytesAsync(pipeReader, domainNameLength, cancellationToken);
                socket5Result.Address = Encoding.UTF8.GetString(addressBytes.Span);

                if (IPAddress.TryParse(socket5Result.Address, out var address) && address.AddressFamily == System.Net.Sockets.AddressFamily.InterNetworkV6)
                {
                    //ipv6
                    socket5Result.Address = $"[{socket5Result.Address}]";
                }
            }
            else if (socket5Header.Value.Atype == 0x01)
            {
                //ipv4
                using var ipv4Bytes = await ReadBytesAsync(pipeReader, 4, cancellationToken);
                socket5Result.Address = new IPAddress(ipv4Bytes.Span).ToString();
            }
            else if (socket5Header.Value.Atype == 0x04)
            {
                //ipv6
                using var ipv6Bytes = await ReadBytesAsync(pipeReader, 16, cancellationToken);
                socket5Result.Address = $"[{new IPAddress(ipv6Bytes.Span)}]";
            }
            else
            {
                throw new Exception($"不支持的 atype {socket5Header.Value.Atype}");
            }

            cancellationToken.ThrowIfCancellationRequested();
            //读取大端格式的端口
            socket5Result.Port = await pipeReader.ReadBigEndianAsync<ushort>(cancellationToken);
            return socket5Result;
        }
    }
}