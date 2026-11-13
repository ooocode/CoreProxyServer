using System.Text.Json.Serialization;

namespace CoreProxy.Server.Orleans
{
    [JsonSerializable(typeof(string))]
    internal partial class AppJsonSerializerContext : JsonSerializerContext;
}
