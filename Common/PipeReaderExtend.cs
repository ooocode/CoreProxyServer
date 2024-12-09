﻿using System.Buffers;
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
        public static async IAsyncEnumerable<ReadOnlyMemory<byte>> ReadAllAsync(PipeReader reader, [EnumeratorCancellation] CancellationToken token = default)
        {
            ReadResult result;
            ReadOnlySequence<byte> buffer;
            do
            {
                result = await reader.ReadAsync(token).ConfigureAwait(false);

                buffer = result.Buffer;
                var consumed = buffer.End;

                try
                {
                    if (buffer.IsSingleSegment)
                    {
                        yield return buffer.First;
                    }
                    else
                    {
                        //yield return buffer.ToArray();
                        //yield return buffer.ToArray();
                        // 创建默认的内存池
                        var memoryPool = MemoryPool<byte>.Shared;

                        var length = (int)buffer.Length;
                        // 租用一个内存块（至少 1024 字节）
                        using var memoryOwner = memoryPool.Rent(length);

                        // 获取租用的内存
                        var memory = memoryOwner.Memory;

                        buffer.CopyTo(memory.Span);

                        yield return memory.Slice(0, length);
                    }
                }
                finally
                {
                    reader.AdvanceTo(consumed);
                }
            }
            while (!result.IsCompleted);
        }

        //public static async IAsyncEnumerable<ReadOnlyMemory<byte>> ReadAllAsync(PipeReader pipeReader, [EnumeratorCancellation] CancellationToken cancellationToken)
        //{
        //    while (!cancellationToken.IsCancellationRequested)
        //    {
        //        var result = await pipeReader.ReadAsync(cancellationToken).ConfigureAwait(false);
        //        var buffer = result.Buffer;
        //        var end = buffer.End;
        //        if (buffer.IsEmpty)
        //        {
        //            break;
        //        }

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
        //            pipeReader.AdvanceTo(end);
        //        }

        //        if (result.IsCompleted || result.IsCanceled)
        //        {
        //            break;
        //        }
        //    }
        //}

    }
}
