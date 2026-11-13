using CoreProxy.Server.Orleans.Models;
using System.Net;
using System.Net.NetworkInformation;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;

namespace CoreProxy.Server.Orleans.Startup
{
    /// <summary>
    /// 服务端证书与客户端口令的生成/读取。逻辑与原先内联在 Program.cs 时逐字一致。
    /// </summary>
    internal static class CertificateFactory
    {
        public static X509Certificate2 LoadOrCreate(string baseDirectory)
        {
            //　在本地调试和发布都没有问题，但是通过IIS发布到服务器上之后发现出现找不到文件路径错误。是由于IIS应用程序池中的【加载用户配置文件】选项默认为False。解决此问题需要将该配置修改为True。即可解决该问题。
            string sslDir = Path.Combine(baseDirectory, "ssl");
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

            // 现代浏览器（Chrome 58+ / Edge / Safari）已不再用 CN 匹配主机名，只认 SubjectAltName；
            // 只有 CN 的证书在浏览器里会直接报 ERR_CERT_COMMON_NAME_INVALID。
            // 证书在首次启动时于本机生成，所以把本机主机名、回环和所有本机网卡地址都写进 SAN，
            // 浏览器用 IP 访问（管理页 / 反代）时也能匹配上。
            var san = new SubjectAlternativeNameBuilder();
            san.AddDnsName(cn);
            san.AddDnsName("localhost");
            san.AddDnsName(Environment.MachineName);
            foreach (var address in GetLocalIpAddresses())
            {
                san.AddIpAddress(address);
            }
            req.CertificateExtensions.Add(san.Build());

            // 以下三项是浏览器/客户端校验服务端证书时的常规要求：
            // BasicConstraints 标明这是终端证书而非 CA；KeyUsage 覆盖 TLS 两种签名/加密用途；
            // EKU(serverAuth) 显式声明可作服务端证书（缺失时有的客户端会直接拒绝）。
            req.CertificateExtensions.Add(new X509BasicConstraintsExtension(false, false, 0, true));
            req.CertificateExtensions.Add(new X509KeyUsageExtension(
                X509KeyUsageFlags.DigitalSignature | X509KeyUsageFlags.KeyEncipherment, true));
            req.CertificateExtensions.Add(new X509EnhancedKeyUsageExtension(
                new OidCollection { new("1.3.6.1.5.5.7.3.1") }, false));
            req.CertificateExtensions.Add(new X509SubjectKeyIdentifierExtension(req.PublicKey, false));

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

        public static CertificatePassword ComputePassword(X509Certificate2 certificate, string baseDirectory)
        {
            var cerBytes = certificate.GetRawCertData();
            var bytes = SHA256.HashData(cerBytes);
            var result = Convert.ToHexString(bytes);

            string fileName = Path.Combine(baseDirectory, "ssl", "client-password.txt");
            File.WriteAllText(fileName, result);
            return new CertificatePassword
            {
                Password = result
            };
        }

        private static IEnumerable<IPAddress> GetLocalIpAddresses()
        {
            // 回环地址优先写入（localhost / 127.0.0.1 / ::1 访问场景）
            var seen = new HashSet<IPAddress>();
            var result = new List<IPAddress>();
            void Add(IPAddress address)
            {
                if ((address.AddressFamily is AddressFamily.InterNetwork or AddressFamily.InterNetworkV6) && seen.Add(address))
                {
                    result.Add(address);
                }
            }

            Add(IPAddress.Loopback);
            Add(IPAddress.IPv6Loopback);

            try
            {
                // 取所有已启用网卡的单播地址：浏览器用局域网 IP / 公网 IP 直连时靠这些 SAN 匹配。
                // 枚举失败（容器、受限权限）不影响启动，至少还有回环地址。
                foreach (var nic in NetworkInterface.GetAllNetworkInterfaces())
                {
                    if (nic.OperationalStatus != OperationalStatus.Up || nic.NetworkInterfaceType == NetworkInterfaceType.Loopback)
                    {
                        continue;
                    }

                    foreach (var unicast in nic.GetIPProperties().UnicastAddresses)
                    {
                        Add(unicast.Address);
                    }
                }
            }
            catch (NetworkInformationException)
            {
            }

            return result;
        }
    }
}
