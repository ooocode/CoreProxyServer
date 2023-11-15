using System;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;

namespace ServerWebApplication.Common
{
    public class RsaService
    {
        private readonly X509Certificate2 certificate2;

        public RsaService(X509Certificate2 certificate2)
        {
            this.certificate2 = certificate2;
        }

        public byte[] Encrypt(byte[] bytes)
        {
            using var publicKey = certificate2.GetRSAPublicKey();
            ArgumentNullException.ThrowIfNull(publicKey, nameof(publicKey));
            var encryData = publicKey.Encrypt(bytes, RSAEncryptionPadding.Pkcs1);
            return encryData;
        }

        public byte[] Decrypt(byte[] bytes)
        {
            using var privateKey = certificate2.GetRSAPrivateKey();
            ArgumentNullException.ThrowIfNull(privateKey, nameof(privateKey));
            var deBytes = privateKey.Decrypt(bytes, RSAEncryptionPadding.Pkcs1);
            return deBytes;
        }
    }
}
