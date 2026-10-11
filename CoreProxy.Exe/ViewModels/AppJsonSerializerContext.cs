using Hello;
using Microsoft.AspNetCore.Http;
using System.Text.Json.Serialization;

namespace CoreProxy.ViewModels
{
    [JsonSerializable(typeof(List<ServerItem>))]
    [JsonSerializable(typeof(ServerItem))]
    [JsonSerializable(typeof(HttpValidationProblemDetails))]
    // [JsonSerializable(typeof(ConnectRequest))]
    [JsonSerializable(typeof(HttpData))]
    // [JsonSerializable(typeof(SendRequest))]
    [JsonSerializable(typeof(HttpSendJson))]
    [JsonSerializable(typeof(ConnectionInfo))]
    internal partial class AppJsonSerializerContext : JsonSerializerContext
    {

    }
}
