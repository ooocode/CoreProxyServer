using Microsoft.AspNetCore.Connections;
using System.Collections.Concurrent;

namespace CoreProxy.Server.Orleans.Internal
{
    public class GlobalState
    {
        public static readonly ConcurrentDictionary<string, ConnectionContext> Sockets = [];
    }
}