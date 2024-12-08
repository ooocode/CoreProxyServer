using Hello;

namespace ServerWebApplication.Services
{
    public interface IEncryptService
    {
        /// <summary>
        /// 加密
        /// </summary>
        /// <param name="password"></param>
        /// <param name="plaintext"></param>
        /// <returns></returns>
        SendDataRequest Encrypt(string password, ReadOnlyMemory<byte> plaintext);

        /// <summary>
        /// 解密
        /// </summary>
        /// <param name="password"></param>
        /// <param name="sendDataRequest"></param>
        /// <returns></returns>
        ReadOnlyMemory<byte> Decrypt(string password, SendDataRequest sendDataRequest);
    }
}
