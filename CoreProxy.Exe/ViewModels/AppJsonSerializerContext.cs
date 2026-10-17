using Hello;
using Microsoft.AspNetCore.Http;
using System.Text.Json.Serialization;

namespace CoreProxy.ViewModels
{
    [JsonSerializable(typeof(List<ServerItem>))]
    [JsonSerializable(typeof(ServerItem))]
    [JsonSerializable(typeof(HttpValidationProblemDetails))]
    [JsonSerializable(typeof(HttpData))]
    internal partial class AppJsonSerializerContext : JsonSerializerContext
    {

    }
}
