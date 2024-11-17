using Microsoft.AspNetCore.Connections;
using Microsoft.AspNetCore.Server.Kestrel.Core;
using ServerWebApplication.Impl;
using System.Diagnostics.CodeAnalysis;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Text;

namespace ServerWebApplication
{
    public class Program
    {
        private static WebApplication? app = null;

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

            builder.WebHost.ConfigureKestrel(serverOptions =>
            {
                serverOptions.AddServerHeader = false; // 禁用 Server 头
                serverOptions.ConfigureEndpointDefaults(c =>
                {
                    c.Protocols = HttpProtocols.Http2;
                });
                serverOptions.ConfigureHttpsDefaults(c =>
                {
                    c.ServerCertificate = certificate2;
                });
            });

            builder.WebHost.UseKestrelHttpsConfiguration();

            const string typeName = "Microsoft.AspNetCore.Server.Kestrel.Transport.Sockets.SocketConnectionFactory";
            var factoryType = typeof(Microsoft.AspNetCore.Server.Kestrel.Transport.Sockets.SocketTransportOptions).Assembly.GetType(typeName);
            ArgumentNullException.ThrowIfNull(factoryType, nameof(factoryType));
            builder.Services.AddSingleton(typeof(IConnectionFactory), factoryType);

            builder.Services.AddSingleton(clientPassword);

            builder.Services.AddGrpc(c =>
            {
                //c.ResponseCompressionAlgorithm = "gzip";
                //c.ResponseCompressionLevel = CompressionLevel.SmallestSize; //System.IO.Compression.CompressionLevel.SmallestSize;

                c.MaxReceiveMessageSize = null;
                c.EnableDetailedErrors = true;
            });

            app = builder.Build();
            app.Logger.LogInformation("客户端连接密码:" + clientPassword.Password);

            app.UseGrpcWeb();
            app.MapGrpcService<ProcessImpl>().EnableGrpcWeb();
            //app.MapGrpcService<ProcessImpl>();
            //app.MapGrpcService<ChatImpl>();

            app.MapGet("/", new RequestDelegate(async (httpContext) =>
            {
                var text = """
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

            Main(args.ToArray());
        }

        [UnmanagedCallersOnly(EntryPoint = "ServiceStop", CallConvs = [typeof(CallConvCdecl)])]
        public static void ServiceStop()
        {
            app?.StopAsync();
        }

        private static X509Certificate2 GetCertificate()
        {
            string fileName = Path.Combine(AppContext.BaseDirectory, "certificate.pfx");
            string password = "apz8fwga";
            if (File.Exists(fileName))
            {
                var certificate = new X509Certificate2(fileName, password);
                return certificate;
            }

            using (var rsa = RSA.Create(4096))
            {
                var req = new CertificateRequest("cn=Microsofter.learn.com ", rsa, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1);
                req.CertificateExtensions.Add(new X509KeyUsageExtension(X509KeyUsageFlags.DigitalSignature, true));

                var cert = req.CreateSelfSigned(DateTimeOffset.Now.AddDays(-1), DateTimeOffset.Now.AddYears(5));

                var rawBytes = cert.Export(X509ContentType.Pfx, password);
                var certificate = new X509Certificate2(rawBytes, password, X509KeyStorageFlags.PersistKeySet | X509KeyStorageFlags.Exportable);

                File.WriteAllBytes(fileName, rawBytes);
                return certificate;
            }
        }

        private static CertificatePassword GetCertificatePassword(X509Certificate2 certificate2)
        {
            using var sha256 = SHA256.Create();
            var cerBytes = certificate2.GetRawCertData().Reverse().ToArray();
            var bytes = sha256.ComputeHash(cerBytes);
            var result = BitConverter.ToString(bytes).Replace("-", "");

            string fileName = Path.Combine(AppContext.BaseDirectory, "client-password.txt");
            File.WriteAllText(fileName, result);
            return new CertificatePassword(result);
        }
    }

    public record CertificatePassword(string Password);
}