public class AutoExitBackgroundService : BackgroundService
{
    private readonly IHostApplicationLifetime hostApplicationLifetime;

    public AutoExitBackgroundService(IHostApplicationLifetime hostApplicationLifetime)
    {
        this.hostApplicationLifetime = hostApplicationLifetime;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        var dir = Path.Combine(Path.GetTempPath(), "0ExitLogs");
        if (!Directory.Exists(dir))
        {
            Directory.CreateDirectory(dir);
        }

        using PeriodicTimer timer = new PeriodicTimer(TimeSpan.FromMinutes(30));
        while (await timer.WaitForNextTickAsync(stoppingToken))
        {
            if (IsBeijingTimeBetween3And5AM())
            {
                var fileName = Path.Combine(dir, DateTimeOffset.Now.ToString("yyyyMMdd") + ".txt");
                if (!File.Exists(fileName))
                {
                    await File.WriteAllTextAsync(fileName, DateTimeOffset.Now.ToString(), stoppingToken);
                    hostApplicationLifetime.StopApplication();
                    timer.Dispose();
                }
            }
        }
    }

    static bool IsBeijingTimeBetween3And5AM()
    {
        // 获取当前 UTC 时间并转换为北京时间（UTC+8）
        DateTimeOffset beijingTime = DateTimeOffset.UtcNow.ToOffset(TimeSpan.FromHours(8));
        // 获取小时数
        int hour = beijingTime.Hour;
        return hour >= 3 && hour <= 5;
    }
}