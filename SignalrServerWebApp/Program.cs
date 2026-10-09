using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.SignalR;
using Microsoft.EntityFrameworkCore;
using SignalrServerWebApp;
using SignalrServerWebApp.Models;
using SignalrServerWebApp.Workers;

var builder = WebApplication.CreateBuilder(args);
builder.Services.AddSingleton(new ServerId { Id = Guid.NewGuid().ToString("N") });

var connectionString = builder.Configuration.GetConnectionString("DefaultConnection");
ArgumentException.ThrowIfNullOrEmpty(connectionString, "DefaultConnection connection string is not configured.");
builder.Services.AddDbContextFactory<AppDbContext>(ctx =>
{
    ctx.UseNpgsql(connectionString);
});

var redisConnectionString = builder.Configuration.GetConnectionString("Redis");
ArgumentException.ThrowIfNullOrWhiteSpace(redisConnectionString, "Redis connection string is not configured.");
builder.Services.AddSignalR(opt =>
{
    opt.EnableDetailedErrors = true;
}).AddStackExchangeRedis(redisConnectionString);

// Add services to the container.
builder.Services.AddHostedService<CleanBackgroundService>();

var app = builder.Build();
// Configure the HTTP request pipeline.

app.MapHub<DeviceHub>("/deviceHub");
app.MapGet("/ls", async (IDbContextFactory<AppDbContext> dbContextFactory,
    CancellationToken cancellationToken) =>
{
    await using var dbContext = await dbContextFactory.CreateDbContextAsync(cancellationToken);
    var devices = await dbContext.Devices.AsNoTracking().ToListAsync(cancellationToken);
    return devices.OrderByDescending(d => d.OnlineDateTime).ToList();
});

app.MapPost("/invoke", async ([FromBody] InvokeRequest request, IDbContextFactory<AppDbContext> dbContextFactory,
        ILogger<DeviceHub> logger, ServerId serverId,
        IHubContext<DeviceHub> hubContext,
        CancellationToken cancellationToken) =>
{
    try
    {
        var result = await hubContext.Clients.Client(request.Id)
            .InvokeAsync<string>(request.MethodName, request.Args ?? [], cancellationToken);
        return new InvokeResponse
        {
            IsSuccess = true,
            Result = result,
            ErrorMessage = string.Empty
        };
    }
    catch (Exception ex)
    {
        return new InvokeResponse
        {
            IsSuccess = false,
            Result = string.Empty,
            ErrorMessage = ex.Message
        };
    }
});

app.Run();


public record InvokeRequest
{
    public required string Id { get; set; }
    public required string MethodName { get; set; }

    public string[]? Args { get; set; }
}


public record InvokeResponse
{
    public bool IsSuccess { get; set; }
    public string? Result { get; set; }

    public string? ErrorMessage { get; set; }
}
