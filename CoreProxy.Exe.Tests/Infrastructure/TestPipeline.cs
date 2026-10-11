using System.IO.Pipelines;

namespace CoreProxy.Exe.Tests.Infrastructure
{
    /// <summary>管道测试辅助：构造输入、按分片写入、精确读取。</summary>
    public static class TestPipeline
    {
        public static Pipe FromBytes(params byte[][] chunks)
        {
            var pipe = new Pipe();
            foreach (var chunk in chunks)
            {
                chunk.CopyTo(pipe.Writer.GetSpan(chunk.Length));
                pipe.Writer.Advance(chunk.Length);
            }

            pipe.Writer.Complete();
            return pipe;
        }

        /// <summary>模拟真实网络分片：逐字节写入并 flush</summary>
        public static async Task WriteFragmentedAsync(PipeWriter writer, ReadOnlyMemory<byte> data, CancellationToken cancellationToken)
        {
            for (var i = 0; i < data.Length; i++)
            {
                await writer.WriteAsync(data.Slice(i, 1), cancellationToken);
            }
        }

        /// <summary>读到 writer 完成为止</summary>
        public static async Task<byte[]> ReadAllAsync(PipeReader reader, CancellationToken cancellationToken)
        {
            using var stream = new MemoryStream();
            while (true)
            {
                var result = await reader.ReadAsync(cancellationToken);
                foreach (var segment in result.Buffer)
                {
                    stream.Write(segment.Span);
                }

                reader.AdvanceTo(result.Buffer.End);
                if (result.IsCompleted)
                {
                    return stream.ToArray();
                }
            }
        }

        /// <summary>读满指定字节数（用于断言服务端应答）</summary>
        public static async Task<byte[]> ReadExactlyAsync(PipeReader reader, int count, CancellationToken cancellationToken)
        {
            using var stream = new MemoryStream();
            while (stream.Length < count)
            {
                var result = await reader.ReadAsync(cancellationToken);
                var buffer = result.Buffer;
                var take = (int)Math.Min(count - stream.Length, buffer.Length);
                if (take > 0)
                {
                    stream.Write(System.Buffers.BuffersExtensions.ToArray(buffer.Slice(0, take)));
                }

                reader.AdvanceTo(buffer.GetPosition(take));

                if (result.IsCompleted && stream.Length < count)
                {
                    break;
                }
            }

            return stream.ToArray();
        }
    }
}
