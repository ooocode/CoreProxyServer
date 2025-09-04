using Hello;
using System.Text.Json.Serialization;

namespace CoreProxy.ViewModels
{
    [JsonSerializable(typeof(HttpData))]
    internal partial class AppJsonSerializerContext : JsonSerializerContext;
}
