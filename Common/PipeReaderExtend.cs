using System.Buffers;
using System.IO.Pipelines;
using System.Runtime.CompilerServices;

namespace ServerWebApplication.Common
{
    public static class PipeReaderExtend
    {
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

                        yield return reusableBuffer.AsMemory()[..length];
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