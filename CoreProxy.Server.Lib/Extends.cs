using CoreProxy.Server.Lib.Impl;
using CoreProxy.Server.Lib.Options;
using CoreProxy.Server.Lib.Services;
using CoreProxy.Server.Lib.Workers;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Connections;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using System.Diagnostics.CodeAnalysis;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;

namespace ServerWebApplication
{
    public static partial class Extends
    {
        [LoggerMessage(Level = LogLevel.Information, Message = "客户端连接密码: {password}")]
        private static partial void LogPassword(ILogger logger, string password);

        [UnconditionalSuppressMessage("Trimming", "IL2026", Justification = "此调用在运行时安全")]
        [UnconditionalSuppressMessage("Trimming", "IL2072", Justification = "此调用在运行时安全")]
        public static void AddProxy(this IServiceCollection services, TransportOptions transportOptions)
        {
            //设置允许不安全的HTTP2支持
            AppContext.SetSwitch("System.Net.Http.SocketsHttpHandler.Http2UnencryptedSupport", true);

            if (Microsoft.Extensions.Hosting.Systemd.SystemdHelpers.IsSystemdService())
            {
                services.AddHostedService<AutoExitBackgroundService>();
            }

            var certificate2 = GetCertificate();
            var clientPassword = GetCertificatePassword(certificate2);
            services.AddSingleton(clientPassword);

            const string typeName = "Microsoft.AspNetCore.Server.Kestrel.Transport.Sockets.SocketConnectionFactory";
            var factoryType = typeof(Microsoft.AspNetCore.Server.Kestrel.Transport.Sockets.SocketTransportOptions).Assembly.GetType(typeName);
            ArgumentNullException.ThrowIfNull(factoryType, nameof(factoryType));
            services.AddSingleton(typeof(IConnectionFactory), factoryType);

            services.Configure<TransportOptions>(c =>
            {
                c.EnableDataEncrypt = transportOptions.EnableDataEncrypt;
                c.UseMax4096Bytes = transportOptions.UseMax4096Bytes;
            });
            services.AddSingleton<DnsParseService>();

            services.AddGrpc();
        }

        public static void MapProxy(this WebApplication app)
        {
            var certificatePassword = app.Services.GetRequiredService<CertificatePassword>();
            LogPassword(app.Logger, certificatePassword.Password);
            app.MapGrpcService<ProcessImpl>();
        }

        private static X509Certificate2 GetCertificate()
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