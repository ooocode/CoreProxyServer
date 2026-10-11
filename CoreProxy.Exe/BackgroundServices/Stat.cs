using Prometheus;

namespace CoreProxy.BackgroundServices
{
    public static class Stat
    {
        public static readonly Gauge ClientCount = Metrics
             .CreateGauge("grpc_stream_client_client", "client连接数");

        public static readonly Gauge ServerCount = Metrics
            .CreateGauge("grpc_stream_clients_server", "server连接数");
    }
}