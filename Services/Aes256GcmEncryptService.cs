using Google.Protobuf;
using Hello;
using System.Security.Cryptography;

namespace ServerWebApplication.Services
{
    public class Aes256GcmEncryptService : IEncryptService
    {
        /// <summary>
        /// AES256-GCM 加密
        /// </summary>
        /// <param name="password"></param>
        /// <param name="plaintext"></param>
        /// <returns></returns>
        public SendDataRequest Encrypt(string password, ReadOnlyMemory<byte> plaintext)
        {
            ReadOnlyMemory<byte> key = Convert.FromBase64String(password);

            // 生成一个随机的初始化向量
            byte[] nonce = RandomNumberGenerator.GetBytes(AesGcm.NonceByteSizes.MaxSize);

            // 创建一个 AesGcm 实例
            using AesGcm aesGcm = new(key[32..].Span, AesGcm.TagByteSizes.MaxSize);

            var ciphertext = new byte[plaintext.Length];
            var tag = new byte[AesGcm.TagByteSizes.MaxSize];
            // 加密数据
            aesGcm.Encrypt(nonce, plaintext.Span, ciphertext, tag, null);

            return new SendDataRequest
            {
                Data = UnsafeByteOperations.UnsafeWrap(ciphertext),
                Nonce = UnsafeByteOperations.UnsafeWrap(nonce),
                Tag = UnsafeByteOperations.UnsafeWrap(tag)
            };
        }

        /// <summary>
        /// 解密
        /// </summary>
        /// <param name="password"></param>
        /// <param name="sendDataRequest"></param>
        /// <returns></returns>
        public ReadOnlyMemory<byte> Decrypt(string password, SendDataRequest sendDataRequest)
        {
            ReadOnlyMemory<byte> key = Convert.FromBase64String(password);
            // 创建一个 AesGcm 实例
            using AesGcm aesGcm = new(key[32..].Span, AesGcm.TagByteSizes.MaxSize);

            var plaintextBytes = new byte[sendDataRequest.Data.Length];

            // 加密数据
            aesGcm.Decrypt(sendDataRequest.Nonce.Memory.Span, sendDataRequest.Data.Memory.Span, sendDataRequest.Tag.Memory.Span, plaintextBytes);
            return plaintextBytes;
        }
    }
}
