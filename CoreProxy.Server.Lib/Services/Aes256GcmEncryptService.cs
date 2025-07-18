using CommunityToolkit.HighPerformance.Buffers;
using System.Security.Cryptography;

namespace CoreProxy.Server.Lib.Services
{
    public static class Aes256GcmEncryptService
    {
        /// <summary>
        /// AES256-GCM 加密
        /// </summary>
        /// <param name="passwordKey"></param>
        /// <param name="plaintext"></param>
        /// <returns></returns>
        public static MemoryOwner<byte> Encrypt(ReadOnlySpan<byte> passwordKey, ReadOnlySpan<byte> plaintext)
        {
            // 生成一个随机的初始化向量
            Span<byte> nonce = stackalloc byte[AesGcm.NonceByteSizes.MaxSize];
            RandomNumberGenerator.Fill(nonce);

            // 创建一个 AesGcm 实例
            using AesGcm aesGcm = new(passwordKey, AesGcm.TagByteSizes.MaxSize);

            Span<byte> tag = stackalloc byte[AesGcm.TagByteSizes.MaxSize];

            //加密后的数据  nonce-tag-ciphertext
            var ciphertext = MemoryOwner<byte>.Allocate(nonce.Length + tag.Length + plaintext.Length);

            // 加密数据
            aesGcm.Encrypt(nonce, plaintext, ciphertext.Span[(nonce.Length + tag.Length)..], tag);

            nonce.CopyTo(ciphertext.Span);
            tag.CopyTo(ciphertext.Span[nonce.Length..]);
            return ciphertext;
        }

        /// <summary>
        /// 解密
        /// </summary>
        /// <param name="passwordKey"></param>
        /// <param name="ciphertext"></param>
        /// <returns></returns>
        public static MemoryOwner<byte> Decrypt(ReadOnlySpan<byte> passwordKey, ReadOnlySpan<byte> ciphertext)
        {
            // 创建一个 AesGcm 实例
            using AesGcm aesGcm = new(passwordKey, AesGcm.TagByteSizes.MaxSize);

            var plaintextBytes = MemoryOwner<byte>.Allocate(ciphertext.Length - AesGcm.NonceByteSizes.MaxSize - AesGcm.TagByteSizes.MaxSize);

            // 解密数据
            var nonce = ciphertext[..AesGcm.NonceByteSizes.MaxSize];
            var tag = ciphertext.Slice(AesGcm.NonceByteSizes.MaxSize, AesGcm.TagByteSizes.MaxSize);
            var ciphertextData = ciphertext[(AesGcm.NonceByteSizes.MaxSize + AesGcm.TagByteSizes.MaxSize)..];
            aesGcm.Decrypt(nonce, ciphertextData, tag, plaintextBytes.Span);
            return plaintextBytes;
        }
    }
}
