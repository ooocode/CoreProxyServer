
using CoreProxy.Server.Orleans.Services;
using Microsoft.AspNetCore.SignalR;

namespace CoreProxy.Server.Orleans.Internal
{
    public class SignalRConnectTargetServerService(IHubContext<ChatHub> hubContext, string host) : IConnectTargetServerService
    {
        public async Task ConnectAsync(CancellationToken cancellationToken)
        {
            var result = await hubContext.Clients.Client(host).InvokeAsync<string>("OnClientHandler", cancellationToken);
        }

        public ValueTask DisposeAsync()
        {
            throw new NotImplementedException();
        }

        public IAsyncEnumerable<ReadOnlyMemory<byte>> ReceiveAsync(CancellationToken cancellationToken)
        {
            throw new NotImplementedException();
        }

        public Task SendAsync(ReadOnlyMemory<byte> data, CancellationToken cancellationToken)
        {
            throw new NotImplementedException();
        }
    }
}
