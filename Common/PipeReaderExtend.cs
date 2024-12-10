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
            PipeReader reader,
            [EnumeratorCancellation] CancellationToken token = default)
        {

            ReadResult result;
            do
            {
                result = await reader.ReadAsync(token).ConfigureAwait(false);

                var buffer = result.Buffer;
                var consumed = buffer.End;

                byte[]? reusableBuffer = null;
                try
                {
                    if (buffer.IsSingleSegment)
                    {
                        yield return buffer.First;
                    }
                    else
                    {
                        var length = (int)buffer.Length;
                        reusableBuffer = ArrayPool<byte>.Shared.Rent(length);
                        buffer.CopyTo(reusableBuffer);

                        yield return reusableBuffer.AsMemory().Slice(0, length);
                    }
                }
                finally
                {
                    if (reusableBuffer != null)
                    {
                        ArrayPool<byte>.Shared.Return(reusableBuffer);
                    }
                    reader.AdvanceTo(consumed);
                }
            } while (!result.IsCompleted);
        }
    }
}