using Google.Protobuf;
using Hello;

namespace ServerWebApplication.Services
{
    public class EmptyEncryptService : IEncryptService
    {
        public SendDataRequest Encrypt(string password, ReadOnlyMemory<byte> plaintext)
        {
            return new SendDataRequest
            {
                Data = UnsafeByteOperations.UnsafeWrap(plaintext),
                Nonce = ByteString.Empty,
                Tag = ByteString.Empty
            };
        }

        public ReadOnlyMemory<byte> Decrypt(string password, SendDataRequest sendDataRequest)
        {
            return sendDataRequest.Data.Memory;
        }
    }
}
