using CoreProxy.Server.Orleans.Models;
using CoreProxy.Server.Orleans.Services;
using CoreProxy.ViewModels;
using Microsoft.AspNetCore.Connections;
using Microsoft.AspNetCore.Server.Kestrel.Core;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Text;

var builder = WebApplication.CreateSlimBuilder(args);
var certificate2 = GetCertificate();
var certificatePassword = GetCertificatePassword(certificate2);
builder.Services.AddSingleton(certificatePassword);

builder.WebHost.ConfigureKestrel(serverOptions =>
{
    serverOptions.AddServerHeader = false; // 禁用 Server 头
    serverOptions.ConfigureEndpointDefaults(c => c.Protocols = HttpProtocols.Http1AndHttp2AndHttp3);

    serverOptions.ConfigureHttpsDefaults(s => s.ServerCertificate = certificate2);
});

builder.WebHost.UseKestrelHttpsConfiguration();

if (Microsoft.Extensions.Hosting.WindowsServices.WindowsServiceHelpers.IsWindowsService())
{
    builder.Host.UseWindowsService();
}
else if (Microsoft.Extensions.Hosting.Systemd.SystemdHelpers.IsSystemdService())
{
    builder.Host.UseSystemd();
}

const string typeName = "Microsoft.AspNetCore.Server.Kestrel.Transport.Sockets.SocketConnectionFactory";

var factoryType = typeof(Microsoft.AspNetCore.Server.Kestrel.Transport.Sockets.SocketTransportOptions).Assembly.GetType(typeName);
ArgumentNullException.ThrowIfNull(factoryType, nameof(factoryType));
builder.Services.AddSingleton(typeof(IConnectionFactory), factoryType);
builder.Services.AddGrpc(opt => opt.EnableDetailedErrors = true);

builder.Services.AddSignalR().AddJsonProtocol(opt => opt.PayloadSerializerOptions = AppJsonSerializerContext.Default.Options);
var app = builder.Build();

app.MapGet("/", new RequestDelegate(async (httpContext) =>
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

    httpContext.Response.ContentType = "text/html; charset=utf-8";
    await httpContext.Response.WriteAsync(text.Trim(), Encoding.UTF8);
}));

app.UseGrpcWeb();
app.MapGrpcService<MyGrpcService>().EnableGrpcWeb();
app.MapHub<ChatHub>("/ChatHub");
app.Run();

static X509Certificate2 GetCertificate()
{
    //　在本地调试和发布都没有问题，但是通过IIS发布到服务器上之后发现出现找不到文件路径错误。是由于IIS应用程序池中的【加载用户配置文件】选项默认为False。解决此问题需要将该配置修改为True。即可解决该问题。
    string fileName = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "certificate.pfx");
    const string password = "apz8fwga";
    if (File.Exists(fileName))
    {
#if NET9_0_OR_GREATER
        return X509CertificateLoader.LoadPkcs12FromFile(fileName, password);
#else
                return new X509Certificate2(fileName, password);
#endif
    }

    using var rsa = RSA.Create(4096);
    var req = new CertificateRequest("cn=Microsofter.learn.com ", rsa, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1);
    req.CertificateExtensions.Add(new X509KeyUsageExtension(X509KeyUsageFlags.DigitalSignature, true));

    var cert = req.CreateSelfSigned(DateTimeOffset.Now.AddDays(-1), DateTimeOffset.Now.AddYears(5));

    var rawBytes = cert.Export(X509ContentType.Pfx, password);
#if NET9_0_OR_GREATER
    var certificate = X509CertificateLoader.LoadPkcs12(rawBytes, password, X509KeyStorageFlags.PersistKeySet | X509KeyStorageFlags.Exportable);
#else
            var certificate = new X509Certificate2(rawBytes, password);
#endif
    File.WriteAllBytes(fileName, rawBytes);
    return certificate;
}

static CertificatePassword GetCertificatePassword(X509Certificate2 certificate2)
{
    var cerBytes = certificate2.GetRawCertData();
    var bytes = SHA256.HashData(cerBytes);
    var result = Convert.ToHexString(bytes);

    string fileName = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "client-password.txt");
    File.WriteAllText(fileName, result);
    return new CertificatePassword
    {
        Password = result
    };
}