using Microsoft.AspNetCore.Connections.Features;
using Microsoft.AspNetCore.SignalR;
using Microsoft.EntityFrameworkCore;
using SignalrServerWebApp.Models;

namespace SignalrServerWebApp.Workers
{
    public class CleanBackgroundService(IDbContextFactory<AppDbContext> dbContextFactory,
        ILogger<DeviceHub> logger, ServerId serverId,
        IHubContext<DeviceHub> hubContext) : BackgroundService
    {
        public override async Task StartAsync(CancellationToken cancellationToken)
        {
            //创建数据库
            await using var dbContext = await dbContextFactory.CreateDbContextAsync(cancellationToken);
            await dbContext.Database.EnsureCreatedAsync(cancellationToken);

            await base.StartAsync(cancellationToken);
        }

        protected override async Task ExecuteAsync(CancellationToken stoppingToken)
        {
            //第一次启动时立即清理过期设备
            await CleanDevicesAsync(stoppingToken);

            //每60秒清理一次过期设备
            using var timer = new PeriodicTimer(TimeSpan.FromSeconds(60));
            while (await timer.WaitForNextTickAsync(stoppingToken))
            {
                try
                {
                    await CleanDevicesAsync(stoppingToken);
                }
                catch (Exception ex)
                {
                    logger.LogError(ex, "清理过期设备时发生异常");
                }
            }
        }

        private async Task CleanDevicesAsync(CancellationToken cancellationToken)
        {
            var dt = DateTime.UtcNow.AddSeconds(-10);
            await using var dbContext = await dbContextFactory.CreateDbContextAsync(cancellationToken);
            var devices = await dbContext.Devices.AsNoTracking()
                .Where(x => x.OnlineDateTime < dt).ToListAsync(cancellationToken);
            foreach (var device in devices)
            {
                if (await IsDeviceOfflineAsync(device.Id, cancellationToken))
                {
                    //删除离线的设备
                    await dbContext.Devices
                        .Where(d => d.Id == device.Id)
                        .ExecuteDeleteAsync(cancellationToken);
                    logger.LogInformation("清理过期设备{id}", device.Id);
                }
            }
        }

        /// <summary>
        /// 检查设备是否离线
        /// </summary>
        /// <param name="id"></param>
        /// <param name="cancellationToken"></param>
        /// <returns></returns>
        private async Task<bool> IsDeviceOfflineAsync(string id, CancellationToken cancellationToken)
        {
            try
            {
                var pingResult = await hubContext.Clients.Client(id).InvokeAsync<string>("ping", Array.Empty<string>(), cancellationToken);
            }
            catch (IOException ex)
            {
                if (ex.Message.Contains("does not exist."))
                {
                    return true;
                }
            }
            catch (HubException)
            {
                //忽略 HubException 异常
            }

            return false;
        }
    }
}
