using CoreProxy.BackgroundServices;
using CoreProxy.ViewModels;
using Microsoft.AspNetCore.Http.Connections.Client;
using Microsoft.AspNetCore.SignalR.Client;
using Microsoft.Extensions.DependencyInjection;
using System.Net;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading.Tasks;

namespace CoreProxy
{
    public class ConnectionInfo
    {
        public required string UserName { get; set; }

        public required string OsVersion { get; set; }
    }


    public static class Program
    {
        public static async Task Main(string[] args)
        {
            /*ConnectionInfo connectionInfo = new ConnectionInfo
            {
                OsVersion = RuntimeInformation.OSDescription,
                UserName = Environment.UserName
            };

            var info = System.Text.Json.JsonSerializer.Serialize(connectionInfo, AppJsonSerializerContext.Default.Options);
            var infoBase64 = Convert.ToBase64String(Encoding.UTF8.GetBytes(info));
          
            string serverHttpAddress = $"http://114.132.102.38:5005/deviceHub?info={infoBase64}";
            await using HubConnection hubConnection = new HubConnectionBuilder()
                     .WithAutomaticReconnect(new InfiniteReconnectPolicy())
                     .WithUrl(serverHttpAddress, (HttpConnectionOptions opt) =>
                     {
                         opt.HttpMessageHandlerFactory = (c) =>
                         {
                             HttpClientHandler httpClientHandler = (c as HttpClientHandler)!;
                             httpClientHandler.ServerCertificateCustomValidationCallback = (__, _, ___, errors) => true;
                             return c;
                         };

                         opt.WebSocketConfiguration = (c) =>
                         {
                             //c.HttpVersion = HttpVersion.Version20;
                             //c.HttpVersionPolicy = HttpVersionPolicy.RequestVersionExact;
                             /*if (serverItem.RequestHttpVersion == RequestHttpVersion.Auto)
                             {
                                 c.HttpVersion = HttpVersion.Version11;
                                 c.HttpVersionPolicy = HttpVersionPolicy.RequestVersionOrHigher;
                             }
                             else if (serverItem.RequestHttpVersion == RequestHttpVersion.Version11)
                             {
                                 c.HttpVersion = HttpVersion.Version11;
                                 c.HttpVersionPolicy = HttpVersionPolicy.RequestVersionExact;
                             }
                             else if (serverItem.RequestHttpVersion == RequestHttpVersion.Version20)
                             {
                                 c.HttpVersion = HttpVersion.Version20;
                                 c.HttpVersionPolicy = HttpVersionPolicy.RequestVersionExact;
                             }
                             else if (serverItem.RequestHttpVersion == RequestHttpVersion.Version30)
                             {
                                 c.HttpVersion = HttpVersion.Version30;
                                 c.HttpVersionPolicy = HttpVersionPolicy.RequestVersionExact;
                             }
                         };
                     })
                  .AddJsonProtocol(config => config.PayloadSerializerOptions = AppJsonSerializerContext.Default.Options).Build();

            hubConnection.On("ping", (string[] args) =>
            {
                return Task.FromResult($"Ok {DateTimeOffset.Now.ToString("O")}");
            });

            hubConnection.On("qqqq", (string[] args) =>
            {
                return Task.FromResult(DateTimeOffset.Now.ToString("O"));
            });*/

            //hubConnection.Closed += HubConnection_Closed;
            //await hubConnection.StartAsync();

            //await Task.Delay(-1);

            //https://github.com/dotnet/runtime/pull/103351
            List<ServerItem> serverItems = [];
            serverItems.Add(ServerItem.Create(1079, "https://192.129.133.136:6099", "7C5C35DD6DA2CD5EE22637C46E02058726124B32449732EF0FB4A71DCB7E8575"));
            //serverItems.Add(ServerItem.Create(1079, "https://66.103.193.238:443", "7C5C35DD6DA2CD5EE22637C46E02058726124B32449732EF0FB4A71DCB7E8575", RequestHttpVersion.Version20));
            //serverItems.Add(ServerItem.Create(1079, "https://localhost:6099", "7C5C35DD6DA2CD5EE22637C46E02058726124B32449732EF0FB4A71DCB7E8575", RequestHttpVersion.Version20));

            //serverItems.Add(ServerItem.Create(1079, "https://[2607:f130:0:103:ff:ff:f3e1:5d84]:5044", "7C5C35DD6DA2CD5EE22637C46E02058726124B32449732EF0FB4A71DCB7E8575", RequestHttpVersion.Version30));



            serverItems.Add(ServerItem.Create(1081, "https://116.252.25.12:5005", "591470F5269D67122EC31E59967DB59A2F077930E527A6672CFC7669520F1D89"));
            serverItems.Add(ServerItem.Create(1084, "https://10.240.132.4:5005", "591470F5269D67122EC31E59967DB59A2F077930E527A6672CFC7669520F1D89"));

            serverItems.Add(ServerItem.Create(1082, "http://oa.zwovo.xyz:19981", "F2C827426C1D00187941361E3BFBA12764D6294CB8D75D1E873E6FCBF85F8EE8", RequestHttpVersion.Version20));

            serverItems.Add(ServerItem.Create(1083, "https://10.240.4.68:5001", "B6244BBD5E42BCFEA742AE8BE930DB7B7C7325F1A9A1EA28ACED21942BAA963B"));
            serverItems.Add(ServerItem.Create(1085, "https://59.211.236.38:5001", "B6244BBD5E42BCFEA742AE8BE930DB7B7C7325F1A9A1EA28ACED21942BAA963B"));

            serverItems.Add(ServerItem.Create(1090, "https://172.16.10.197:6099", "F888EAE226441B99F50FC47CFAE75A9FF904E178762B2BAE873865098D249CBF"));

            await MainFunc.StartMain(serverItems, args);
        }

        private static Task HubConnection_Closed(Exception? arg)
        {
            return Task.CompletedTask;
        }
    }


    public record HttpSendJson
    {
        public required string SessionId { get; set; }
        public required string Payload { get; set; }
    }
}