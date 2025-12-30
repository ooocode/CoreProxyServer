using System.Collections.Concurrent;

namespace CoreProxy.Server.Orleans.Internal
{
    public static class GlobalState
    {
        public static readonly ConcurrentDictionary<string, ConnectItem> Connections = [];
    }

    public class ConnectItem
    {
        public required string ClientIpAddress { get; set; }

        public required DateTimeOffset DateTime { get; set; }
    }
}