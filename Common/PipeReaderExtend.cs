using System.Buffers;
using System.IO.Pipelines;
using System.Runtime.CompilerServices;

namespace ServerWebApplication.Common
{
    public static class PipeReaderExtend
    {
        public static async IAsyncEnumerable<ReadOnlyMemory<byte>> ReadAllAsync(PipeReader pipeReader, [EnumeratorCancellation] CancellationToken cancellationToken)
        {
            while (!cancellationToken.IsCancellationRequested)
            {
                var result = await pipeReader.ReadAsync(cancellationToken).ConfigureAwait(false);
                var buffer = result.Buffer;
                var end = buffer.End;
                if (buffer.IsEmpty)
                {
                    break;
                }

                try
                {
                    if (buffer.IsSingleSegment)
                    {
                        yield return buffer.First;
                    }
                    else
                    {
                        yield return buffer.ToArray();
                    }
                }
                finally
                {
                    pipeReader.AdvanceTo(end);
                }

                if (result.IsCompleted || result.IsCanceled)
                {
                    break;
                }
            }
        }
    }
}
