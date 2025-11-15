using CoreProxy.Server.Orleans;
using CoreProxy.Server.Orleans.BackgroundServices;
using CoreProxy.Server.Orleans.Models;
using CoreProxy.Server.Orleans.Services;
using Microsoft.AspNetCore.Server.Kestrel.Core;
using Microsoft.AspNetCore.Server.Kestrel.Transport.Sockets;
using Microsoft.Extensions.Options;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Text;
using System.Text.Json.Serialization;

var builder = WebApplication.CreateSlimBuilder(args);

var certificate2 = GetHttpsCertificate();
var certificatePassword = GetCertificatePassword(certificate2);

builder.Services.AddSingleton(certificatePassword);

builder.WebHost.ConfigureKestrel(serverOptions =>
{
    //如果服务器在此时间段内没有收到任何帧，则服务器会向客户端发送保持活动 ping
    serverOptions.Limits.Http2.KeepAlivePingDelay = TimeSpan.FromSeconds(45);

    //如果服务器在此超时期间没有收到任何帧（如响应 ping），则连接将关闭。
    serverOptions.Limits.Http2.KeepAlivePingTimeout = TimeSpan.FromSeconds(135);

    if (int.TryParse(builder.Configuration["Http2MaxStreamsPerConnection"], out int maxStreams) && maxStreams > 0)
    {
        serverOptions.Limits.Http2.MaxStreamsPerConnection = maxStreams;
    }

    serverOptions.AddServerHeader = false; // 禁用 Server 头
    serverOptions.ConfigureEndpointDefaults(c => c.Protocols = HttpProtocols.Http2);

    serverOptions.ConfigureHttpsDefaults(s => s.ServerCertificate = certificate2);
});

//builder.WebHost.UseQuic();
builder.WebHost.UseKestrelHttpsConfiguration();

if (Microsoft.Extensions.Hosting.WindowsServices.WindowsServiceHelpers.IsWindowsService())
{
    builder.Host.UseWindowsService();
}
else if (Microsoft.Extensions.Hosting.Systemd.SystemdHelpers.IsSystemdService())
{
    builder.Host.UseSystemd();
    builder.Services.AddHostedService<AutoExitBackgroundService>();
}

//const string typeName = "Microsoft.AspNetCore.Server.Kestrel.Transport.Sockets.SocketConnectionFactory";
//var factoryType = typeof(Microsoft.AspNetCore.Server.Kestrel.Transport.Sockets.SocketTransportOptions).Assembly.GetType(typeName);
//ArgumentNullException.ThrowIfNull(factoryType, nameof(factoryType));
//builder.Services.AddSingleton(typeof(IConnectionFactory), factoryType);
//

builder.Services.AddSingleton(s =>
{
    var opt = s.GetRequiredService<IOptions<SocketTransportOptions>>();
    var logger = s.GetRequiredService<ILogger<SocketConnectionContextFactory>>();
    return new SocketConnectionContextFactory(new SocketConnectionFactoryOptions
    {
        IOQueueCount = opt.Value.IOQueueCount,
        MaxReadBufferSize = opt.Value.MaxReadBufferSize,
        MaxWriteBufferSize = opt.Value.MaxWriteBufferSize,
        UnsafePreferInlineScheduling = opt.Value.UnsafePreferInlineScheduling,
        WaitForDataBeforeAllocatingBuffer = opt.Value.WaitForDataBeforeAllocatingBuffer,
    }, logger);
});

builder.Services.AddGrpc(opt => opt.EnableDetailedErrors = true);

//添加Linux测试工具
builder.Services.AddHostedService<LinuxTestBackgroundService>();
builder.Services.AddSignalR(opt => opt.EnableDetailedErrors = true)
    .AddJsonProtocol(opt => opt.PayloadSerializerOptions = AppJsonSerializerContext.Default.Options);

builder.Services.AddDataProtection();

var app = builder.Build();

app.MapGet("/", () =>
{
    const string text = """
                <!DOCTYPE html>
                <html>
                <head>
                <title>Welcome to nginx!</title>
                <style>
                html { color-scheme: light dark; }
                body { width: 35em; margin: 0 auto;
                font-family: Tahoma, Verdana, Arial, sans-serif; }
                </style>
                </head>
                <body>
                <h1>Welcome to nginx!</h1>
                <p>If you see this page, the nginx web server is successfully installed and
                working. Further configuration is required.</p>

                <p>For online documentation and support please refer to
                <a href="http://nginx.org/">nginx.org</a>.<br/>
                Commercial support is available at
                <a href="http://nginx.com/">nginx.com</a>.</p>

                <p><em>Thank you for using nginx.</em></p>
                </body>
                </html>
                """;
    return TypedResults.Content(text, "text/html", Encoding.UTF8);
});

app.MapGrpcService<MyGrpcService>();
app.MapHub<ChatHub>("/chathub");
app.Run();

X509Certificate2 GetHttpsCertificate()
{
    //　在本地调试和发布都没有问题，但是通过IIS发布到服务器上之后发现出现找不到文件路径错误。是由于IIS应用程序池中的【加载用户配置文件】选项默认为False。解决此问题需要将该配置修改为True。即可解决该问题。
    string sslDir = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "ssl");
    if (!Directory.Exists(sslDir))
    {
        Directory.CreateDirectory(sslDir);
    }

    string pdxFileName = Path.Combine(sslDir, "https-certificate.pfx");
    string passwordFileName = Path.Combine(sslDir, "https-certificate-password.txt");
    if (File.Exists(pdxFileName) && File.Exists(passwordFileName))
    {
        var passowrd = File.ReadAllText(passwordFileName).Trim();
#if NET9_0_OR_GREATER
        return X509CertificateLoader.LoadPkcs12FromFile(pdxFileName, passowrd);
#else
        return new X509Certificate2(pdxFileName, password);
#endif
    }

    using var rsa = RSA.Create(4096);

    string cn = $"{RandomNumberGenerator.GetString("abcdefghijklmnopqrstuvwxyz0123456789", 10)}.org";
    var req = new CertificateRequest($"cn={cn}", rsa, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1);
    req.CertificateExtensions.Add(new X509KeyUsageExtension(X509KeyUsageFlags.DigitalSignature, true));

    var cert = req.CreateSelfSigned(DateTimeOffset.Now.AddDays(-1), DateTimeOffset.Now.AddYears(5));

    var password = Guid.NewGuid().ToString("N");
    var rawBytes = cert.Export(X509ContentType.Pfx, password);
#if NET9_0_OR_GREATER
    var certificate = X509CertificateLoader.LoadPkcs12(rawBytes, password, X509KeyStorageFlags.PersistKeySet | X509KeyStorageFlags.Exportable);
#else
    var certificate = new X509Certificate2(rawBytes, password);
#endif
    File.WriteAllBytes(pdxFileName, rawBytes);
    File.WriteAllText(passwordFileName, password);
    return certificate;
}

CertificatePassword GetCertificatePassword(X509Certificate2 certificate2)
{
    var cerBytes = certificate2.GetRawCertData();
    var bytes = SHA256.HashData(cerBytes);
    var result = Convert.ToHexString(bytes);

    string fileName = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "ssl", "client-password.txt");
    File.WriteAllText(fileName, result);
    return new CertificatePassword
    {
        Password = result
    };
}

namespace CoreProxy.Server.Orleans
{
    [JsonSerializable(typeof(string))]
    internal partial class AppJsonSerializerContext : JsonSerializerContext;
}