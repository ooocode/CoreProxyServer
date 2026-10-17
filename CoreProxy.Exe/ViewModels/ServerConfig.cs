using Microsoft.Extensions.Configuration;

namespace CoreProxy.ViewModels
{
    /// <summary>服务器列表的来源：exe 同目录下 appsettings.json 的 Servers 节点</summary>
    public static class ServerConfig
    {
        public const string SectionName = "Servers";

        //ServerItem 上的 required 成员没法用配置绑定源生成器（生成的对象初始值设定项过不了编译），这里手工解析
        private static readonly string[] _knownKeys =
        [
            nameof(ServerItem.LocalListenerPort),
            nameof(ServerItem.ServerHttpAddress),
            nameof(ServerItem.Password)
        ];

        /// <summary>读取指定目录下的 appsettings.json，文件不存在直接抛异常</summary>
        public static IConfigurationRoot LoadConfiguration(string basePath)
        {
            return new ConfigurationBuilder()
                .SetBasePath(basePath)
                .AddJsonFile("appsettings.json", optional: false, reloadOnChange: false)
                .Build();
        }

        /// <summary>把 Servers 节点解析成服务器列表，写错任何一处都立刻报错，不带默认值蒙混过去</summary>
        public static List<ServerItem> Load(IConfiguration configuration)
        {
            var section = configuration.GetSection(SectionName);
            if (!section.Exists())
            {
                throw new InvalidOperationException($"appsettings.json 里缺少 {SectionName} 节点");
            }

            var serverItems = section.GetChildren()
                .Select(Parse)
                .ToList();
            if (serverItems.Count is 0)
            {
                throw new InvalidOperationException($"appsettings.json 的 {SectionName} 节点里一个服务器都没配");
            }

            //端口同时用作 gRPC 客户端的名，重复时只有先注册的那个能生效
            var repeatedPort = serverItems.GroupBy(static x => x.LocalListenerPort)
                .FirstOrDefault(static x => x.Count() > 1);
            if (repeatedPort is not null)
            {
                throw new InvalidOperationException(
                    $"appsettings.json 的 {SectionName} 节点里 LocalListenerPort={repeatedPort.Key} 重复了");
            }

            return serverItems;
        }

        private static ServerItem Parse(IConfigurationSection section)
        {
            var item = $"{SectionName}:{section.Key}";

            //拼错属性名会让该项静默变成默认值，这里直接拦下来
            var unknownKey = section.GetChildren()
                .FirstOrDefault(x => !_knownKeys.Contains(x.Key, StringComparer.OrdinalIgnoreCase));
            if (unknownKey is not null)
            {
                throw new InvalidOperationException(
                    $"{item} 里出现未知配置项 {unknownKey.Key}，可用项：{string.Join("、", _knownKeys)}");
            }

            return ServerItem.Create(
                localListenerPort: ParseInt(section, item, nameof(ServerItem.LocalListenerPort)),
                serverHttpAddress: Required(section, item, nameof(ServerItem.ServerHttpAddress)),
                password: Required(section, item, nameof(ServerItem.Password)));
        }

        private static string Required(IConfigurationSection section, string item, string key)
        {
            return Value(section, key) ?? throw new InvalidOperationException($"{item} 缺少 {key}");
        }

        private static string? Value(IConfigurationSection section, string key)
        {
            return section[key] is { Length: > 0 } value ? value : null;
        }

        private static int ParseInt(IConfigurationSection section, string item, string key)
        {
            return int.TryParse(Required(section, item, key), out var value)
                ? value
                : throw new InvalidOperationException($"{item} 的 {key} 不是整数：{section[key]}");
        }
    }
}
