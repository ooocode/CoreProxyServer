using CoreProxy.Server.Orleans.Models;
using Microsoft.Extensions.Configuration;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace CoreProxy.Server.Orleans.Tests
{
    [TestClass]
    public sealed class CoreProxyOptionsTests
    {
        private static CoreProxyOptions Parse(params (string Key, string Value)[] values)
        {
            var config = new ConfigurationBuilder()
                .AddInMemoryCollection(values.ToDictionary(x => x.Key, x => (string?)x.Value))
                .Build();
            return CoreProxyOptions.FromConfiguration(config);
        }

        [TestMethod]
        public void EmptyConfiguration_UsesDefaults()
        {
            var options = Parse();

            Assert.AreEqual(TimeSpan.FromSeconds(20), options.ConnectTimeout);
            Assert.AreEqual(TimeSpan.FromSeconds(60), options.DnsCacheTtl);
            Assert.AreEqual(TimeSpan.FromSeconds(900), options.AdminInvokeTimeout);
            Assert.IsNull(options.Http2MaxStreamsPerConnection);
        }

        [DataTestMethod]
        [DataRow("5", 5)]
        [DataRow("0", 20)]
        [DataRow("-3", 20)]
        [DataRow("abc", 20)]
        public void ConnectTimeout_UsesPositiveSecondsOrDefault(string raw, int expectedSeconds)
        {
            var options = Parse(("ConnectTimeoutSeconds", raw));

            Assert.AreEqual(TimeSpan.FromSeconds(expectedSeconds), options.ConnectTimeout);
        }

        [DataTestMethod]
        [DataRow("0", 0)]      // 0 表示禁用 DNS 缓存
        [DataRow("120", 120)]
        [DataRow("-1", 60)]
        [DataRow("abc", 60)]
        public void DnsCacheTtl_UsesNonNegativeSecondsOrDefault(string raw, int expectedSeconds)
        {
            var options = Parse(("TargetDnsCacheTtlSeconds", raw));

            Assert.AreEqual(TimeSpan.FromSeconds(expectedSeconds), options.DnsCacheTtl);
        }

        [DataTestMethod]
        [DataRow("30", 30)]
        [DataRow("0", 900)]
        [DataRow("-1", 900)]
        [DataRow("abc", 900)]
        public void AdminInvokeTimeout_UsesPositiveSecondsOrDefault(string raw, int expectedSeconds)
        {
            var options = Parse(("AdminInvokeTimeoutSeconds", raw));

            Assert.AreEqual(TimeSpan.FromSeconds(expectedSeconds), options.AdminInvokeTimeout);
        }

        [DataTestMethod]
        [DataRow("100", 100)]
        [DataRow("0", 0)]
        [DataRow("-1", 0)]
        [DataRow("abc", 0)]
        public void Http2MaxStreams_UsesPositiveValueOrNull(string raw, int expected)
        {
            var options = Parse(("Http2MaxStreamsPerConnection", raw));

            if (expected == 0)
            {
                Assert.IsNull(options.Http2MaxStreamsPerConnection);
            }
            else
            {
                Assert.AreEqual(expected, options.Http2MaxStreamsPerConnection);
            }
        }
    }
}
