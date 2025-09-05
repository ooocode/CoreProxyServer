using System.Runtime.InteropServices;

namespace CoreProxy.Server.Orleans.BackgroundServices
{
    public class LinuxTestBackgroundService(IHostEnvironment hostEnvironment) : BackgroundService
    {
        protected override async Task ExecuteAsync(CancellationToken stoppingToken)
        {
            if (hostEnvironment.IsProduction() && RuntimeInformation.IsOSPlatform(OSPlatform.Linux))
            {
                var Content = $"lsof -i -a -p {Environment.ProcessId} -r 2";
                var fileName = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "test-lsof.sh");
                await File.WriteAllTextAsync(fileName, Content, stoppingToken);
            }
        }
    }
}