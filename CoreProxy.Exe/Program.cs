using CoreProxy.ViewModels;

namespace CoreProxy
{
    public static class Program
    {
        public static async Task Main(string[] args)
        {
            //https://github.com/dotnet/runtime/pull/103351
            //服务器列表来自 exe 同目录下的 appsettings.json（Servers 节点）
            var serverItems = ServerConfig.Load(ServerConfig.LoadConfiguration(AppContext.BaseDirectory));

            await MainFunc.StartMain(serverItems, args);
        }
    }
}