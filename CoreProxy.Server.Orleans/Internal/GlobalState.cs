using Microsoft.AspNetCore.Connections;
using System.Collections.Concurrent;

namespace CoreProxy.Server.Orleans.Internal
{
    public static class GlobalState
    {
        public static readonly ConcurrentDictionary<string, ConnectItem> Sockets = [];
    }

    public class ConnectItem
    {
        public ConnectionContext? ConnectionContext { get; set; }
        public required string ClientIpAddress { get; set; }

        public required DateTimeOffset DateTime { get; set; }
    }
}