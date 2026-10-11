using CoreProxy.BackgroundServices;
using CoreProxy.Common;
using CoreProxy.Internal;
using CoreProxy.ViewModels;
using DotNext.IO.Pipelines;
using Grpc.Core;
using Grpc.Net.ClientFactory;
using Hello;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Connections;
using Microsoft.AspNetCore.Connections.Features;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Http.Connections.Client;
using Microsoft.AspNetCore.Http.Connections.Features;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.SignalR.Client;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Net.Http.Headers;
using Prometheus;
using System.IO;
using System.IO.Pipelines;
using System.Net;
using System.Net.Http;
using System.Text;
using System.Threading.Tasks;

namespace CoreProxy
{
    public static partial class MainFunc
    {
        private static WebApplication? _app;

        public static async Task StartMain(List<ServerItem> serverItems, string[] args)
        {
            //var options = AppJsonSerializerContext.Default.Options;
            //JsonNode schema = options.GetJsonSchemaAsNode(typeof(ServerConfig));
            //var schemaText = schema.ToJsonString();
            //File.WriteAllText(Path.Combine(AppDomain.CurrentDomain.BaseDirectory, $"config-schema.json"), schemaText);

            //配置文件目录
            var configDir = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "CoreProxyConfigs");
            if (!Directory.Exists(configDir))
            {
                Directory.CreateDirectory(configDir);
            }

            var urls = serverItems.Select(x => $"http://*:{x.LocalListenerPort}")
                .ToList();
            var urlsString = string.Join(";", urls);
            var argsList = args.ToList();
            argsList.Add("--urls=" + urlsString);

            //设置允许不安全的HTTP2支持
            AppContext.SetSwitch("System.Net.Http.SocketsHttpHandler.Http2UnencryptedSupport", true);
            AppContext.SetSwitch("Microsoft.AspNetCore.Server.Kestrel.EnableWindows81Http2", true);

            var builder = WebApplication.CreateSlimBuilder([.. argsList]);
            builder.Services.ConfigureHttpJsonOptions(options => options.SerializerOptions.TypeInfoResolverChain.Insert(0, AppJsonSerializerContext.Default));

            if (Microsoft.Extensions.Hosting.WindowsServices.WindowsServiceHelpers.IsWindowsService())
            {
                builder.Host.UseWindowsService();
            }
            else if (Microsoft.Extensions.Hosting.Systemd.SystemdHelpers.IsSystemdService())
            {
                builder.Host.UseSystemd();
            }

            //builder.Services.AddValidation();
            builder.Services.AddSingleton(serverItems);
            builder.Services.AddSingleton<Socket5ConnectionHandlerService>();
            builder.Services.AddSingleton<GrpcTransportHandlerService>();
            builder.Services.AddSingleton<SignalRTransportHandlerService>();
            builder.WebHost.UseKestrelHttpsConfiguration();
            builder.WebHost.ConfigureKestrel(static serverOptions =>
            {
                serverOptions.ConfigureEndpointDefaults(static (Microsoft.AspNetCore.Server.Kestrel.Core.ListenOptions listenOptions) =>
                {
                    var socket5Service = listenOptions.ApplicationServices.GetRequiredService<Socket5ConnectionHandlerService>();
                    listenOptions.Use(socket5Service.OnConnectedAsync);
                });
            });

            foreach (var serverItem in serverItems)
            {
                builder.Services.AddGrpcClient<Greeter.GreeterClient>(serverItem.LocalListenerPort.ToString(), options => options.Address = new Uri(serverItem.ServerHttpAddress))
                .ConfigureChannel(static (c) =>
                {
                    c.UnsafeUseInsecureChannelCallCredentials = true;
                    c.MaxReceiveMessageSize = null;
                    c.MaxSendMessageSize = null;
                }).AddCallCredentials((_, metadata) =>
                {
                    metadata.Add(HeaderNames.Authorization, $"Password {serverItem.Password}");
                    return Task.CompletedTask;
                })
                .UseSocketsHttpHandler(static (socketsHttpHandler, _) =>
                {
                    //socketsHttpHandler.PooledConnectionIdleTimeout = Timeout.InfiniteTimeSpan;
                    //socketsHttpHandler.KeepAlivePingDelay = TimeSpan.FromSeconds(60);
                    //socketsHttpHandler.KeepAlivePingTimeout = TimeSpan.FromSeconds(30);

                    socketsHttpHandler.EnableMultipleHttp2Connections = true;
                    socketsHttpHandler.EnableMultipleHttp3Connections = true;
                    socketsHttpHandler.SslOptions.RemoteCertificateValidationCallback = static (____, _, ___, __) => true;

                    // 客户端窗口必须与服务端匹配
                    socketsHttpHandler.InitialHttp2StreamWindowSize = 16777216;
                });
            }



            builder.Services.AddHostedService<SignalRConnectBackgroundService>();

            builder.Services.AddHostedService<StatBackgroundService>();
            if (builder.Environment.IsDevelopment())
            {
                builder.Services.AddCors();
            }
            _app = builder.Build();

            if (builder.Environment.IsDevelopment())
            {
                _app.UseCors(x => x.AllowAnyHeader()
                                      .AllowAnyMethod()
                                      .AllowAnyOrigin().SetPreflightMaxAge(TimeSpan.FromHours(1)));
            }

            _app.MapGet("/pac.txt", async (HttpContext httpContext) =>
            {
                ShowPacLog(_app.Logger, httpContext.Connection.RemoteIpAddress?.ToString());
                var pacFile = Path.Combine(builder.Environment.WebRootPath ?? string.Empty, "pac.txt");
                if (!File.Exists(pacFile))
                {
                    return Results.NotFound();
                }
                var pacText = await File.ReadAllLinesAsync(pacFile, httpContext.RequestAborted);
                if (pacText.Length == 0)
                {
                    return TypedResults.NoContent();
                }

                var host = httpContext.Request.Host;
                pacText[0] = $"var proxy = \"SOCKS5 {host.Host}:{host.Port}; SOCKS {host.Host}:{host.Port}; DIRECT;\";\r\n";
                var result = string.Join("\r\n", pacText);
                return TypedResults.Content(result, "text/plain", Encoding.UTF8);
            });

            _app.MapGet("/pac.js", async (HttpContext httpContext) =>
            {
                ShowPacLog(_app.Logger, httpContext.Connection.RemoteIpAddress?.ToString());
                var pacFile = Path.Combine(builder.Environment.WebRootPath ?? string.Empty, "pac.js");
                if (!File.Exists(pacFile))
                {
                    return Results.NotFound();
                }
                var pacText = await File.ReadAllLinesAsync(pacFile, httpContext.RequestAborted);
                if (pacText.Length == 0)
                {
                    return TypedResults.NoContent();
                }

                var host = httpContext.Request.Host;
                pacText[0] = $"var proxy = 'PROXY {host};DIRECT';";
                var result = string.Join("\r\n", pacText);
                return TypedResults.Content(result, "text/plain", Encoding.UTF8);
            });

            _app.MapGet("/metrics", async (httpContext) =>
            {
                await using var memoryStream = new MemoryStream();
                await Metrics.DefaultRegistry.CollectAndExportAsTextAsync(memoryStream, ExpositionFormat.PrometheusText,
                    httpContext.RequestAborted);
                var text = Encoding.UTF8.GetString(memoryStream.ToArray());

                httpContext.Response.ContentType = "text/plain; charset=utf-8";
                await httpContext.Response.WriteAsync(text, Encoding.UTF8);
            });

            var group = _app.MapGroup("/web/api/servers");
            group.MapPost("/", async ([FromBody] ServerItem serverItem) =>
            {
                var fileName = Path.Combine(configDir, $"{serverItem.LocalListenerPort}.txt");
                await File.WriteAllTextAsync(fileName, System.Text.Json.JsonSerializer.Serialize(serverItem, AppJsonSerializerContext.Default.ServerItem));
                return Results.Ok(serverItem);
            });

            group.MapGet("/", async () =>
            {
                var files = Directory.GetFiles(configDir, "*.txt");
                var serverItems = new List<ServerItem>();
                foreach (var file in files)
                {
                    var content = await File.ReadAllTextAsync(file);
                    var serverItem = System.Text.Json.JsonSerializer.Deserialize(content, AppJsonSerializerContext.Default.ServerItem);
                    if (serverItem != null)
                    {
                        serverItems.Add(serverItem);
                    }
                }

                serverItems = serverItems.OrderBy(x => x.LocalListenerPort)
                    .ToList();
                return Results.Ok(serverItems);
            });

            //http代理
            _app.MapMethods("/", [HttpMethods.Connect], async (HttpContext httpContext, CancellationToken cancellationToken) =>
            {
                var targetHost = httpContext.Request.Host.Host;
                var targetPort = httpContext.Request.Host.Port;

                var grpcClientFactory = httpContext.RequestServices.GetRequiredService<GrpcClientFactory>();
                var grpcTransportHandlerService = httpContext.RequestServices.GetRequiredService<GrpcTransportHandlerService>();
                var logger = httpContext.RequestServices.GetRequiredService<ILogger<GrpcTransportHandlerService>>();

                var processGrpcClient = grpcClientFactory.CreateClient<Greeter.GreeterClient>(httpContext.Connection.LocalPort.ToString());

                using var cancellationTokenSource = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
                var targetUrl = $"tcp://{targetHost}:{targetPort}";

                var connectionFeature = httpContext.Features.Get<IConnectionTransportFeature>();
                ArgumentNullException.ThrowIfNull(connectionFeature, nameof(connectionFeature));

                var duplexPipe = connectionFeature.Transport;

                try
                {
                    await grpcTransportHandlerService.DualStreamHandlerAsync(processGrpcClient, duplexPipe,
                    targetUrl, ProxyType.Http,
                    cancellationTokenSource);
                }
                catch (RpcException ex)
                {
                    logger.LogError(ex, $"{targetUrl}");
                }
            });

            _app.Run();
        }

        [LoggerMessage(Level = LogLevel.Information, Message = "{remoteIpAddress} 获取pac.txt文件")]
        private static partial void ShowPacLog(ILogger logger, string? remoteIpAddress);
    }

    public class SignalRetryPolicy : IRetryPolicy
    {
        public TimeSpan? NextRetryDelay(RetryContext retryContext)
        {
            return TimeSpan.FromSeconds(5);
        }
    }
}