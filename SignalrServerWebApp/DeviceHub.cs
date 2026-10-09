using Microsoft.AspNetCore.Connections.Features;
using Microsoft.AspNetCore.SignalR;
using Microsoft.EntityFrameworkCore;
using SignalrServerWebApp.Models;
using System.Text;

namespace SignalrServerWebApp
{
    public class ConnectionInfo
    {
        public required string UserName { get; set; }

        public required string OsVersion { get; set; }
    }


    public class DeviceHub(IDbContextFactory<AppDbContext> dbContextFactory, ILogger<DeviceHub> logger, ServerId serverId) : Hub
    {
        public override async Task OnConnectedAsync()
        {
            var httpContext = Context.GetHttpContext();
            var userNameBase64 = httpContext?.Request.Query["info"].ToString();
            if (string.IsNullOrWhiteSpace(userNameBase64))
            {
                throw new HubException("query parameter [info] is required");
            }

            var infoJson = Encoding.UTF8.GetString(Convert.FromBase64String(userNameBase64));
            var info = System.Text.Json.JsonSerializer.Deserialize<ConnectionInfo>(infoJson);
            ArgumentNullException.ThrowIfNull(info, nameof(info));


            var heartbeat = Context.Features.Get<IConnectionHeartbeatFeature>();
            ArgumentNullException.ThrowIfNull(heartbeat, nameof(heartbeat));
            heartbeat.OnHeartbeat(state =>
            {
                var connectionId = (string)state!;
                logger.LogInformation($"{DateTimeOffset.Now}客户端心跳{Environment.NewLine}ConnectionId={connectionId}");
            }, Context.ConnectionId);

            var cancellationToken = Context.ConnectionAborted;
            await using var dbContext = await dbContextFactory.CreateDbContextAsync(cancellationToken);

            var device = new Device
            {
                Id = Context.ConnectionId,
                ServerId = serverId.Id,
                UserName = info.UserName,
                OsVersion = info.OsVersion,
                UserAgent = httpContext?.Request.Headers.UserAgent.ToString(),
                IpAddress = httpContext?.Connection.RemoteIpAddress?.ToString(),
                OnlineDateTime = DateTimeOffset.UtcNow
            };

            await dbContext.Devices.AddAsync(device, cancellationToken);
            await dbContext.SaveChangesAsync(cancellationToken);

            logger.LogInformation($"客户端上线{Environment.NewLine}UserName={device.UserName}{Environment.NewLine}IpAddress={device.IpAddress}");
        }


        public override async Task OnDisconnectedAsync(Exception? exception)
        {
            var cancellationToken = CancellationToken.None;
            await using var dbContext = await dbContextFactory.CreateDbContextAsync(cancellationToken);
            var device = await dbContext.Devices.FirstOrDefaultAsync(d => d.Id == Context.ConnectionId, cancellationToken);
            if (device != null)
            {
                dbContext.Devices.Remove(device);
                await dbContext.SaveChangesAsync(cancellationToken);

                logger.LogError($"客户端离线{Environment.NewLine}UserName={device.UserName}{Environment.NewLine}IpAddress={device.IpAddress}");
            }
        }
    }
}
