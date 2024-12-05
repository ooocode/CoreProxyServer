using Grpc.Net.Compression;
using System.IO.Compression;

namespace ServerWebApplication.Common
{
    public class BrCompressionProvider : ICompressionProvider
    {
        public const string EncodingNameConst = "br";
        public string EncodingName => EncodingNameConst;

        public Stream CreateCompressionStream(Stream stream, CompressionLevel? compressionLevel)
        {
            return new BrotliStream(stream, compressionLevel ?? CompressionLevel.Optimal, true);
        }

        public Stream CreateDecompressionStream(Stream stream)
        {
            return new BrotliStream(stream, CompressionMode.Decompress);
        }
    }
}