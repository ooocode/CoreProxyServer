using CoreProxy.Server.Orleans.Services;
using Microsoft.AspNetCore.Http;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace CoreProxy.Server.Orleans.Tests
{
    /// <summary>
    /// 代理会话只靠路径 token 与 Referer：不写任何 Cookie，避免浏览器把代理会话带到 /admin。
    /// 顶层导航（Sec-Fetch-Dest: document/iframe/... 或缺失）必须不走 Referer 兜底；
    /// 只有目标页发起的子资源请求（script/style/image/fetch…）才允许。
    /// </summary>
    [TestClass]
    public sealed class YarpProxyRefererTests
    {
        [DataTestMethod]
        [DataRow("http://host/TOKEN/h5/oa", "TOKEN")]
        [DataRow("https://host/TOKEN/", "TOKEN")]
        [DataRow("https://host/TOKEN", "TOKEN")]
        [DataRow("https://host/TOKEN?x=1", "TOKEN")]
        [DataRow("https://host/admin.html", "admin.html")]
        public void RefererFirstSegmentIsExtracted(string referer, string expected)
        {
            Assert.AreEqual(expected, YarpProxyExtensions.GetTokenFromReferer(referer));
        }

        [DataTestMethod]
        [DataRow(null)]
        [DataRow("")]
        [DataRow("not a uri")]
        [DataRow("/relative/path")]
        public void InvalidRefererYieldsEmpty(string? referer)
        {
            Assert.AreEqual(string.Empty, YarpProxyExtensions.GetTokenFromReferer(referer));
        }

        [DataTestMethod]
        [DataRow(null, true)] // 缺失（curl / 旧浏览器）按导航处理，宁可少代理也不劫持导航
        [DataRow("document", true)]
        [DataRow("iframe", true)]
        [DataRow("frame", true)]
        [DataRow("embed", true)]
        [DataRow("object", true)]
        [DataRow("script", false)]
        [DataRow("style", false)]
        [DataRow("image", false)]
        [DataRow("empty", false)] // fetch / XHR
        public void NavigationDetection(string? secFetchDest, bool expected)
        {
            var context = new DefaultHttpContext();
            if (secFetchDest is not null)
            {
                context.Request.Headers["Sec-Fetch-Dest"] = secFetchDest;
            }

            Assert.AreEqual(expected, YarpProxyExtensions.IsNavigationRequest(context.Request));
        }
    }
}
