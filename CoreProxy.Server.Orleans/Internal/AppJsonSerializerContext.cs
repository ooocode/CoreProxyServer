using Hello;
using System.Text.Json.Serialization;

namespace CoreProxy.ViewModels
{
    //[JsonSerializable(typeof(ConnectRequest))]
    [JsonSerializable(typeof(HttpData))]
    //[JsonSerializable(typeof(SendRequest))]
    [JsonSerializable(typeof(HttpSendJson))]
    internal partial class AppJsonSerializerContext : JsonSerializerContext;
}
