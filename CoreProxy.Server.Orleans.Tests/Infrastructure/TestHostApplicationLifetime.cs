using Microsoft.Extensions.Hosting;

namespace CoreProxy.Server.Orleans.Tests.Infrastructure
{
    public sealed class TestHostApplicationLifetime : IHostApplicationLifetime
    {
        private readonly CancellationTokenSource stopping = new();

        public CancellationToken ApplicationStarted => CancellationToken.None;

        public CancellationToken ApplicationStopping => stopping.Token;

        public CancellationToken ApplicationStopped => CancellationToken.None;

        public void StopApplication() => stopping.Cancel();
    }
}
