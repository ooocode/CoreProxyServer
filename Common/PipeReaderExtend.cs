using CommunityToolkit.HighPerformance.Buffers;
using System.Buffers;
using System.IO.Pipelines;
using System.Runtime.CompilerServices;

namespace ServerWebApplication.Common
{
    public static class PipeReaderExtend
    {
        public static IAsyncEnumerable<ReadOnlyMemory<byte>> ReadAllAsync(bool useMax4096Bytes, PipeReader pipeReader, CancellationToken cancellationToken)
        {
            if (useMax4096Bytes)
            {
                return DotNext.IO.Pipelines.PipeExtensions.ReadAllAsync(pipeReader, cancellationToken);
            }
            else
            {
                return ReadAllFastAsync(pipeReader, cancellationToken);
            }
        }

        private static async IAsyncEnumerable<ReadOnlyMemory<byte>> ReadAllFastAsync(
            PipeReader reader,
            [EnumeratorCancellation] CancellationToken token = default)
        {
            ReadResult result;
            do
            {
                result = await reader.ReadAsync(token).ConfigureAwait(false);

                var buffer = result.Buffer;
                var consumed = buffer.End;

                try
                {
                    if (buffer.IsSingleSegment)
                    {
                        yield return buffer.First;
                    }
                    else
                    {
                        using MemoryOwner<byte> reusableBuffer = MemoryOwner<byte>.Allocate((int)buffer.Length);
                        buffer.CopyTo(reusableBuffer.Span);
                        yield return reusableBuffer.Memory;
                    }
                }
                finally
                {
                    reader.AdvanceTo(consumed);
                }
            } while (!result.IsCompleted);
        }
    }
}