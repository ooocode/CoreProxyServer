using Microsoft.AspNetCore.Connections;
using System.Collections.Concurrent;

namespace CoreProxy.Server.Orleans.Internal
{
    public class GlobalState
    {
        public static readonly ConcurrentDictionary<string, ConnectItem> Sockets = [];
    }

    public class ConnectItem
    {
        public required ConnectionContext ConnectionContext { get; set; }
        public required string ClientIpAddress { get; set; }

        public required DateTimeOffset DateTime { get; set; }
    }
}