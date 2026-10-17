using CoreProxy.ViewModels;
using Microsoft.Extensions.Configuration;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using System.IO;
using System.Text;

namespace CoreProxy.Exe.Tests
{
    /// <summary>appsettings.json 的 Servers 节点是服务器列表的唯一来源，解析错了代理就整个起不来</summary>
    [TestClass]
    public sealed class ServerConfigTests
    {
        // ---------------- 仓库里那份真实配置 ----------------

        /// <summary>发出去的那份 appsettings.json 必须能被真正加载：注释、结尾逗号、枚举名都不能把它顶掉</summary>
        [TestMethod]
        public void 仓库里的appsettings配置文件可以正常加载()
        {
            var path = Path.Combine(AppContext.BaseDirectory, "appsettings.json");
            Assert.IsTrue(File.Exists(path), $"测试输出目录里没有 appsettings.json：{path}");

            var serverItems = ServerConfig.Load(ServerConfig.LoadConfiguration(AppContext.BaseDirectory));

            Assert.IsTrue(serverItems.Count > 0);
            foreach (var serverItem in serverItems)
            {
                Assert.IsTrue(Uri.TryCreate(serverItem.ServerHttpAddress, UriKind.Absolute, out _),
                    $"{serverItem.LocalListenerPort} 的 ServerHttpAddress 不是合法绝对地址：{serverItem.ServerHttpAddress}");
                Assert.IsTrue(serverItem.Password.Length > 0, $"{serverItem.LocalListenerPort} 的 Password 是空的");
            }

            Assert.AreEqual(serverItems.Count, serverItems.Select(x => x.LocalListenerPort).Distinct().Count(),
                "同一个端口在列表里出现了两次");
        }

        // ---------------- 解析规则 ----------------

        /// <summary>配置文件里本来就带 // 注释和结尾逗号，JSON 配置提供程序必须跳过它们</summary>
        [TestMethod]
        public void 注释和结尾多余的逗号会被忽略()
        {
            var serverItems = Load(
                """
                {
                  // 端口 1079 指的是内网那台
                  "Servers": [
                    {
                      "LocalListenerPort": 1079, // 行尾注释
                      "ServerHttpAddress": "https://192.129.133.136:6099",
                      "Password": "P1",
                    },
                  ],
                }
                """);

            Assert.AreEqual(1, serverItems.Count);
            Assert.AreEqual(1079, serverItems[0].LocalListenerPort);
            Assert.AreEqual("P1", serverItems[0].Password);
        }

        // ---------------- 写错了要报错，不能静默生效 ----------------

        [TestMethod]
        public void 缺少Servers节点时报错()
        {
            var configuration = BuildConfiguration("""{ "Logging": { "LogLevel": { "Default": "Warning" } } }""");

            var exception = Assert.ThrowsException<InvalidOperationException>(() => ServerConfig.Load(configuration));
            StringAssert.Contains(exception.Message, "Servers");
        }

        /// <summary>空数组等于一个代理端口都不监听，必须报错而不是启动一个空壳</summary>
        [TestMethod]
        public void Servers为空列表时报错()
        {
            var configuration = BuildConfiguration("""{ "Servers": [] }""");

            Assert.ThrowsException<InvalidOperationException>(() => ServerConfig.Load(configuration));
        }

        /// <summary>端口是 gRPC 客户端的名，重复时只有一个能生效，等于另一个服务器被静默丢掉</summary>
        [TestMethod]
        public void 端口重复时报错()
        {
            var configuration = BuildConfiguration(
                """
                {
                  "Servers": [
                    { "LocalListenerPort": 1081, "ServerHttpAddress": "https://a:1", "Password": "P" },
                    { "LocalListenerPort": 1081, "ServerHttpAddress": "https://b:1", "Password": "P" }
                  ]
                }
                """);

            var exception = Assert.ThrowsException<InvalidOperationException>(() => ServerConfig.Load(configuration));
            StringAssert.Contains(exception.Message, "1081");
        }

        /// <summary>属性名拼错会静默落到默认值上（监听端口 0、版本 Auto），所以未知配置项要直接报错</summary>
        [TestMethod]
        public void 配置项名字拼错时报错()
        {
            var configuration = BuildConfiguration(
                """
                {
                  "Servers": [
                    { "LocalLinstenerPort": 1081, "ServerHttpAddress": "https://a:1", "Password": "P" }
                  ]
                }
                """);

            var exception = Assert.ThrowsException<InvalidOperationException>(() => ServerConfig.Load(configuration));
            StringAssert.Contains(exception.Message, "LocalLinstenerPort");
        }

        [TestMethod]
        public void 缺少监听端口时报错()
        {
            var configuration = BuildConfiguration(
                """
                {
                  "Servers": [
                    { "ServerHttpAddress": "https://a:1", "Password": "P" }
                  ]
                }
                """);

            var exception = Assert.ThrowsException<InvalidOperationException>(() => ServerConfig.Load(configuration));
            StringAssert.Contains(exception.Message, "LocalListenerPort");
        }

        [TestMethod]
        public void 缺少密码时报错()
        {
            var configuration = BuildConfiguration(
                """
                {
                  "Servers": [
                    { "LocalListenerPort": 1081, "ServerHttpAddress": "https://a:1" }
                  ]
                }
                """);

            var exception = Assert.ThrowsException<InvalidOperationException>(() => ServerConfig.Load(configuration));
            StringAssert.Contains(exception.Message, "Password");
        }

        [TestMethod]
        public void 监听端口不是整数时报错()
        {
            var configuration = BuildConfiguration(
                """
                {
                  "Servers": [
                    { "LocalListenerPort": "1081a", "ServerHttpAddress": "https://a:1", "Password": "P" }
                  ]
                }
                """);

            var exception = Assert.ThrowsException<InvalidOperationException>(() => ServerConfig.Load(configuration));
            StringAssert.Contains(exception.Message, "LocalListenerPort");
        }

        // ---------------- 辅助 ----------------

        private static List<ServerItem> Load(string json)
        {
            return ServerConfig.Load(BuildConfiguration(json));
        }

        private static IConfigurationRoot BuildConfiguration(string json)
        {
            using var stream = new MemoryStream(Encoding.UTF8.GetBytes(json));
            return new ConfigurationBuilder().AddJsonStream(stream).Build();
        }
    }
}
