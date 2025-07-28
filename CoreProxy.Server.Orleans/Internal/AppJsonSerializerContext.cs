using Hello;
using System.Text.Json.Serialization;

namespace CoreProxy.ViewModels
{
    [JsonSerializable(typeof(ConnectRequest))]
    [JsonSerializable(typeof(HttpData))]
    [JsonSerializable(typeof(SendRequest))]
    internal partial class AppJsonSerializerContext : JsonSerializerContext
    {

    }
}
