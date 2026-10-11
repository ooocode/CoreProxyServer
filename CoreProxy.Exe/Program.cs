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
            //服务器列表来自 exe 同目录下的 appsettings.json（Servers 节点）
            var serverItems = ServerConfig.Load(ServerConfig.LoadConfiguration(AppContext.BaseDirectory));

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