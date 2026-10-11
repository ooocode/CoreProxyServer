using Microsoft.AspNetCore.SignalR.Client;

namespace CoreProxy
{
    public static class GloHubConnections
    {
        public static List<HubConnection> Hubs { get; set; } = [];
    }
}
