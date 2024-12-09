using Microsoft.Extensions.ObjectPool;
using System.Buffers;
using System.IO.Pipelines;
using System.Runtime.CompilerServices;

namespace ServerWebApplication.Common
{
    public static class PipeReaderExtend
    {
        /// <summary>
        /// Reads all chunks of data from the pipe.
        /// </summary>
        /// <param name="reader">The pipe reader.</param>
        /// <param name="token">The token that can be used to cancel the operation.</param>
        /// <returns>A sequence of data chunks.</returns>
        /// <exception cref="OperationCanceledException">The operation has been canceled.</exception>
        //public static async IAsyncEnumerable<ReadOnlyMemory<byte>> ReadAllAsync(PipeReader reader,
        //    [EnumeratorCancellation] CancellationToken token = default)
        //{
        //    ReadResult result;
        //    do
        //    {
        //        result = await reader.ReadAsync(token).ConfigureAwait(false);

        //        var buffer = result.Buffer;
        //        var consumed = buffer.End;

        //        try
        //        {
        //            if (buffer.IsSingleSegment)
        //            {
        //                yield return buffer.First;
        //            }
        //            else
        //            {
        //                yield return buffer.ToArray();
        //            }
        //        }
        //        finally
        //        {
        //            reader.AdvanceTo(consumed);
        //        }
        //    } while (!result.IsCompleted);
        //}

        public static async IAsyncEnumerable<ReadOnlyMemory<byte>> ReadAllAsync(
            ObjectPool<ReusableBuffer> bufferPool,
            PipeReader reader,
            [EnumeratorCancellation] CancellationToken token = default)
        {
            ReadResult result;
            do
            {
                result = await reader.ReadAsync(token).ConfigureAwait(false);

                var buffer = result.Buffer;
                var consumed = buffer.End;

                ReusableBuffer? reusableBuffer = null;
                try
                {
                    if (buffer.IsSingleSegment)
                    {
                        yield return buffer.First;
                    }
                    else
                    {
                        var length = (int)buffer.Length;
                        //yield return buffer.ToArray();
                        // 创建默认的内存池
                        //var memoryPool = MemoryPool<byte>.Shared;


                        //// 租用一个内存块（至少 1024 字节）
                        //using var memoryOwner = memoryPool.Rent(length);

                        //// 获取租用的内存
                        //var memory = memoryOwner.Memory;

                        //buffer.CopyTo(memory.Span);

                        //yield return memory.Slice(0, length);
                        reusableBuffer = bufferPool.Get();
                        buffer.CopyTo(reusableBuffer.Data);

                        Memory<byte> span = reusableBuffer.Data;
                        yield return span.Slice(0, length);
                    }
                }
                finally
                {
                    if (reusableBuffer != null)
                    {
                        bufferPool.Return(reusableBuffer);
                    }
                    reader.AdvanceTo(consumed);
                }
            } while (!result.IsCompleted);
        }
    }
}