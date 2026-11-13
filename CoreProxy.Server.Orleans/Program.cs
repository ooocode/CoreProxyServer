using CoreProxy.Server.Orleans;
using CoreProxy.Server.Orleans.BackgroundServices;
using CoreProxy.Server.Orleans.Endpoints;
using CoreProxy.Server.Orleans.Internal;
using CoreProxy.Server.Orleans.Models;
using CoreProxy.Server.Orleans.Services;
using CoreProxy.Server.Orleans.Startup;
using Microsoft.AspNetCore.Server.Kestrel.Core;
using Microsoft.AspNetCore.SignalR;
using Microsoft.Extensions.Caching.Memory;
using NLog;
using NLog.Web;
//https://www.zhifeiya.cn/post/2026/2/1/d6a548a1


AppContext.SetSwitch("Microsoft.AspNetCore.Server.Kestrel.EnableWindows81Http2", true);

var builder = WebApplication.CreateSlimBuilder(args);

NLog.LogManager.Setup().LoadConfiguration(builder =>
{
    const string errLogFileName = "${basedir}/Logs/${date:format=yyyy-MM-dd}-err.log";
    builder.ForLogger().FilterMinLevel(NLog.LogLevel.Error).WriteToFile(
        fileName: errLogFileName,
        maxArchiveDays: 30  // 仅保留最近30天的文件
     );
});
builder.Host.UseNLog();


var baseDirectory = AppDomain.CurrentDomain.BaseDirectory;
var certificate2 = CertificateFactory.LoadOrCreate(baseDirectory);
var certificatePassword = CertificateFactory.ComputePassword(certificate2, baseDirectory);

// 反代 token 的 AES 密钥直接由证书口令派生：证书重签 → 口令变化 → 旧代理链接全部失效。
ProxyTokenProtector.Configure(certificatePassword.Password);

// /api/invoke 的默认等待上限；单次请求可用 timeoutSeconds 覆盖（只要求 > 0，不设上限）。
// 配置缺失或 ≤0 时回落到 900 秒（15 分钟）。
var coreOptions = CoreProxyOptions.FromConfiguration(builder.Configuration);
builder.Services.AddSingleton(coreOptions);
var adminInvokeTimeout = coreOptions.AdminInvokeTimeout;

builder.Services.AddSingleton(certificatePassword);

builder.Services.AddMemoryCache();

// 隧道/设备登记表：进程内单例，原先的静态全局字典改为注入，便于测试隔离与后续替换实现。
builder.Services.AddSingleton<TunnelRegistry>();
builder.Services.AddSingleton<DeviceRegistry>();

// 管理 API 的强类型 JSON 走 source-gen（AOT 友好），避免 minimal API 在运行时反射序列化。
// 只挂 admin 专用 context（含 InvokeRequest/InvokePayload 等），不掺反射兜底 resolver：
// AOT 发布下 DefaultJsonTypeInfoResolver 会触发 IL2026/IL3050，且所有走 HTTP JSON 的类型都已在此 context 声明。
// 该 context 刻意与 SignalR 共用的 AppJsonSerializerContext 分开（改后者会改掉 SignalR 线上协议格式）。
builder.Services.ConfigureHttpJsonOptions(options =>
    options.SerializerOptions.TypeInfoResolver = AdminJsonContext.Default);

//目标主机名解析缓存：代理里大量隧道指向同一后端，TTL 内只解析一次，把 DNS 从建连关键路径上拿掉。
//默认 60s；配 0 可禁用缓存（每次建连都重新解析）。
builder.Services.AddSingleton(sp => new TargetResolver(
    sp.GetRequiredService<IMemoryCache>(),
    coreOptions.DnsCacheTtl));

int? maxStreamsPerConnection = coreOptions.Http2MaxStreamsPerConnection;

builder.WebHost.ConfigureKestrel(serverOptions =>
    KestrelConfigurator.Configure(serverOptions, builder.Configuration, certificate2, maxStreamsPerConnection));

builder.WebHost.UseKestrelHttpsConfiguration();

builder.Host.UseWindowsService();
builder.Host.UseSystemd();

if (Microsoft.Extensions.Hosting.Systemd.SystemdHelpers.IsSystemdService())
{
    //添加自动重启服务
    builder.Services.AddHostedService<AutoExitBackgroundService>();
}

builder.Services.AddGrpc(options =>
{
    options.MaxSendMessageSize = null;    // 不限制发送消息大小
    options.MaxReceiveMessageSize = null; // 不限制接收消息大小
});

//添加Linux测试工具
builder.Services.AddHostedService<LinuxTestBackgroundService>();
builder.Services.AddSignalR(opt =>
{
    // 不回传服务端异常细节：设备端不需要，只会把内部实现泄漏给客户端
    opt.EnableDetailedErrors = false;
    opt.MaximumReceiveMessageSize = int.MaxValue / 2;
    opt.MaximumParallelInvocationsPerClient = 8192 * 10;
    opt.StreamBufferCapacity = int.MaxValue / 2;
}).AddJsonProtocol(opt => opt.PayloadSerializerOptions = AppJsonSerializerContext.Default.Options);

builder.Services.AddDataProtection();

// 核心步骤：注册 YARP 转发器服务（反向代理的中间件与其实现见 Services/YarpProxy.cs）
builder.Services.AddYarpForwarder();

var app = builder.Build();

app.UseYarpReverseProxy();

app.UseWebSockets();

// 管理页鉴权：/admin 静态文件与 /api/status 一律要求 HTTP Basic 凭据（本机回环也不例外）。
// 必须排在 UseStaticFiles 之前，否则静态文件会先被放出去、鉴权形同虚设。
// 受保护路径的判定见 AdminApi.RequiresAuth（已有回归测试覆盖 /admin.html 这类易漏路径）。
app.Use(async (ctx, next) =>
{
    if (AdminApi.RequiresAuth(ctx.Request.Path))
    {
        if (!AdminApi.CheckBasicAuth(ctx, certificatePassword.Password))
        {
            AdminApi.WriteUnauthorized(ctx);
            return;
        }
    }

    await next();
});

app.UseStaticFiles();

app.MapGrpcService<TunnelGrpcService>();
app.MapHub<ChatHub>("/chathub");

AdminEndpoints.Map(app, certificatePassword, adminInvokeTimeout);
WelcomeEndpoints.Map(app);

app.Run();
