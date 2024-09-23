using System.IO;
using System.IO.Compression;
using Grpc.Net.Compression;

public class BrICompressionProvider : ICompressionProvider
{
    public string EncodingName => "br";

    public Stream CreateCompressionStream(Stream stream, CompressionLevel? compressionLevel)
    {
        return new BrotliStream(stream, compressionLevel ?? CompressionLevel.Optimal);
    }

    public Stream CreateDecompressionStream(Stream stream)
    {
        return new BrotliStream(stream, CompressionMode.Decompress);
    }
}