using System.IO.Pipelines;

namespace CoreProxy.Server.Orleans.Internal
{
    public interface IConnectTargetServerService : IAsyncDisposable
    {
        Task ConnectAsync(CancellationToken cancellationToken);

        ValueTask<FlushResult> SendAsync(ReadOnlyMemory<byte> data, CancellationToken cancellationToken);

        IAsyncEnumerable<ReadOnlyMemory<byte>> ReceiveAsync(CancellationToken cancellationToken);
    }
}