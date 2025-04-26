using Microsoft.AspNetCore.Connections;
using Microsoft.AspNetCore.Server.Kestrel.Core;
using ServerWebApplication.Common;
using ServerWebApplication.Impl;
using ServerWebApplication.Options;
using ServerWebApplication.Services;
using ServerWebApplication.Workers;
using System.Diagnostics.CodeAnalysis;
using System.IO.Compression;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Text;

namespace ServerWebApplication
{
    public static partial class Program
    {
        private static WebApplication? app;

        [LoggerMessage(Level = LogLevel.Information, Message = "客户端连接密码: {password}")]
        private static partial void LogPassword(ILogger logger, string password);

        [UnconditionalSuppressMessage("Trimming", "IL2026", Justification = "此调用在运行时安全")]
        [UnconditionalSuppressMessage("Trimming", "IL2072", Justification = "此调用在运行时安全")]
        public static void Main(string[] args)
        {
            //设置允许不安全的HTTP2支持
            AppContext.SetSwitch("System.Net.Http.SocketsHttpHandler.Http2UnencryptedSupport", true);

            var builder = WebApplication.CreateSlimBuilder(args);
            if (Microsoft.Extensions.Hosting.WindowsServices.WindowsServiceHelpers.IsWindowsService())
            {
                builder.Host.UseWindowsService();
            }
            else if (Microsoft.Extensions.Hosting.Systemd.SystemdHelpers.IsSystemdService())
            {
                builder.Host.UseSystemd();
                builder.Services.AddHostedService<AutoExitBackgroundService>();
            }

            var certificate2 = GetCertificate();
            var clientPassword = GetCertificatePassword(certificate2);
            builder.Services.AddSingleton(clientPassword);

            builder.WebHost.ConfigureKestrel(serverOptions =>
            {
                serverOptions.AddServerHeader = false; // 禁用 Server 头
                serverOptions.ConfigureEndpointDefaults(c => c.Protocols = HttpProtocols.Http2);
                serverOptions.ConfigureHttpsDefaults(c => c.ServerCertificate = certificate2);
            });

            builder.WebHost.UseKestrelHttpsConfiguration();

            const string typeName = "Microsoft.AspNetCore.Server.Kestrel.Transport.Sockets.SocketConnectionFactory";
            var factoryType = typeof(Microsoft.AspNetCore.Server.Kestrel.Transport.Sockets.SocketTransportOptions).Assembly.GetType(typeName);
            ArgumentNullException.ThrowIfNull(factoryType, nameof(factoryType));
            builder.Services.AddSingleton(typeof(IConnectionFactory), factoryType);

            builder.Services.Configure<TransportOptions>(builder.Configuration.GetSection(nameof(TransportOptions)));
            builder.Services.AddSingleton<DnsParseService>();

            builder.Services.AddGrpc(c =>
            {
                c.CompressionProviders.Add(new BrCompressionProvider());
                c.ResponseCompressionAlgorithm = BrCompressionProvider.EncodingNameConst;
                c.ResponseCompressionLevel = CompressionLevel.Optimal;
            });

            app = builder.Build();
            LogPassword(app.Logger, clientPassword.Password);

            app.MapGrpcService<ProcessImpl>();

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

            //app.MapGet("/metrics", new RequestDelegate(async (httpContext) =>
            //{
            //    using var memoryStream = new MemoryStream();
            //    await Metrics.DefaultRegistry.CollectAndExportAsTextAsync(memoryStream, ExpositionFormat.PrometheusText, httpContext.RequestAborted);
            //    var text = Encoding.UTF8.GetString(memoryStream.ToArray());

            //    httpContext.Response.ContentType = "text/plain; charset=utf-8";
            //    await httpContext.Response.WriteAsync(text);
            //}));
            app.Run();
        }

        [UnmanagedCallersOnly(EntryPoint = "ServiceMain", CallConvs = [typeof(CallConvCdecl)])]
        public static unsafe void ServiceMain(int argc, nint* argv)
        {
            List<string> args = [];
            for (int i = 0; i < argc; i++)
            {
                var arg = Marshal.PtrToStringAnsi(argv[i]);
                if (!string.IsNullOrWhiteSpace(arg))
                {
                    args.Add(arg);
                }
            }

            Main([.. args]);
        }

        [UnmanagedCallersOnly(EntryPoint = "ServiceStop", CallConvs = [typeof(CallConvCdecl)])]
        public static void ServiceStop()
        {
            app?.StopAsync();
        }

        private static X509Certificate2 GetCertificate()
        {
            string fileName = Path.Combine(AppContext.BaseDirectory, "certificate.pfx");
            const string password = "apz8fwga";
            if (File.Exists(fileName))
            {
                return X509CertificateLoader.LoadPkcs12FromFile(fileName, password);
            }

            using var rsa = RSA.Create(4096);
            var req = new CertificateRequest("cn=Microsofter.learn.com ", rsa, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1);
            req.CertificateExtensions.Add(new X509KeyUsageExtension(X509KeyUsageFlags.DigitalSignature, true));

            var cert = req.CreateSelfSigned(DateTimeOffset.Now.AddDays(-1), DateTimeOffset.Now.AddYears(5));

            var rawBytes = cert.Export(X509ContentType.Pfx, password);
            var certificate = X509CertificateLoader.LoadPkcs12(rawBytes, password, X509KeyStorageFlags.PersistKeySet | X509KeyStorageFlags.Exportable);

            File.WriteAllBytes(fileName, rawBytes);
            return certificate;
        }

        private static CertificatePassword GetCertificatePassword(X509Certificate2 certificate2)
        {
            var cerBytes = certificate2.GetRawCertData();
            var bytes = SHA256.HashData(cerBytes);
            var result = Convert.ToHexString(bytes);

            string fileName = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "client-password.txt");
            File.WriteAllText(fileName, result);
            return new CertificatePassword(result);
        }
    }

    public class CertificatePassword
    {
        public string Password { get; set; }

        public Memory<byte> PasswordKey { get; }

        public CertificatePassword(string password)
        {
            Password = password;
            PasswordKey = Convert.FromHexString(password);
        }
    };
}