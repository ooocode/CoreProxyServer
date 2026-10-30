using CoreProxy.Server.Orleans.Services;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using System.Text;

namespace CoreProxy.Server.Orleans.Tests
{
    /// <summary>
    /// 反代 token 的加解密与防伪造边界。
    /// 这些用例钉住两件事：token 必须能还原目标地址；伪造/篡改/旧 hex 一律不能进代理分支。
    /// </summary>
    [TestClass]
    public sealed class ProxyTokenProtectorTests
    {
        [TestInitialize]
        public void Setup() => ProxyTokenProtector.Configure("unit-test-secret");

        [DataTestMethod]
        [DataRow("http://127.0.0.1:8123")]
        [DataRow("https://12315.scjdglj.gxzf.gov.cn:5000")]
        [DataRow("http://[::1]:8080")]
        [DataRow("https://www.youtube.com")]
        public void RoundTrip(string host)
        {
            var token = ProxyTokenProtector.Encrypt(host);

            Assert.IsTrue(ProxyTokenProtector.TryDecrypt(token, out var decoded), "合法 token 应能解密");
            Assert.AreEqual(host, decoded);
        }

        [TestMethod]
        public void TokenIsUrlSafeAndDoesNotLeakHost()
        {
            var token = ProxyTokenProtector.Encrypt("http://10.240.4.68");

            Assert.IsTrue(token.All(c => char.IsAsciiLetterOrDigit(c) || c is '-' or '_'), $"含非 URL-safe 字符: {token}");
            Assert.IsFalse(token.Contains("10.240"), $"token 泄漏明文地址: {token}");
        }

        [TestMethod]
        public void SameHostProducesDifferentTokens()
        {
            var first = ProxyTokenProtector.Encrypt("http://10.240.4.68");
            var second = ProxyTokenProtector.Encrypt("http://10.240.4.68");

            Assert.AreNotEqual(first, second, "随机 nonce 应让同一地址每次生成不同 token");
        }

        [TestMethod]
        public void TamperedTokenIsRejected()
        {
            var token = ProxyTokenProtector.Encrypt("http://10.240.4.68");
            var last = token[^1];
            var tampered = token[..^1] + (last == 'A' ? 'B' : 'A');

            Assert.IsFalse(ProxyTokenProtector.TryDecrypt(tampered, out _), "篡改后的 token 必须被拒绝");
        }

        /// <summary>
        /// 旧 hex(@host@) 链接必须不再被接受——否则任何人都能自行构造任意目标的代理地址。
        /// 同时锁住 "Base64Url 解码抛 FormatException → 500" 这个回归：这里要求安静返回 false。
        /// </summary>
        [TestMethod]
        public void LegacyHexAddressIsRejectedWithoutThrowing()
        {
            var legacyHex = Convert.ToHexStringLower(Encoding.UTF8.GetBytes("@http://127.0.0.1:8123@"));

            Assert.IsFalse(ProxyTokenProtector.TryDecrypt(legacyHex, out _));
        }

        [DataTestMethod]
        [DataRow("")]
        [DataRow("A")]
        [DataRow("AAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAA")]
        [DataRow("not-a-token-with-invalid-chars!")]
        public void GarbageInputIsRejectedWithoutThrowing(string input)
        {
            Assert.IsFalse(ProxyTokenProtector.TryDecrypt(input, out _));
        }

        [TestMethod]
        public void TokenSignedWithAnotherKeyIsRejected()
        {
            var token = ProxyTokenProtector.Encrypt("http://10.240.4.68");

            ProxyTokenProtector.Configure("another-secret");

            Assert.IsFalse(ProxyTokenProtector.TryDecrypt(token, out _), "换密钥后旧 token 必须失效");
        }

        [TestMethod]
        public void ConfigureRejectsNullSecret()
        {
            Assert.ThrowsException<ArgumentNullException>(() => ProxyTokenProtector.Configure(null!));
        }

        [DataTestMethod]
        [DataRow("")]
        [DataRow("   ")]
        public void ConfigureRejectsBlankSecret(string secret)
        {
            Assert.ThrowsException<ArgumentException>(() => ProxyTokenProtector.Configure(secret));
        }
    }
}
