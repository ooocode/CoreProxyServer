using CoreProxy.Server.Orleans.Services;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace CoreProxy.Server.Orleans.Tests
{
    /// <summary>
    /// 设备上线 info 的解析：明文 JSON（非 protobuf）。非法/缺 deviceId 必须抛，
    /// 由 SignalR 拒连接，避免往在线表塞脏数据。键名大小写不敏感。
    /// </summary>
    [TestClass]
    public sealed class DeviceInfoTests
    {
        [TestMethod]
        public void Parse_CamelCaseJson_ReadsAllFields()
        {
            var d = DeviceInfo.Parse("""{"deviceId":"d1","machineName":"pc-1","userName":"u1","methods":["SessionStart","Reboot"]}""");

            Assert.AreEqual("d1", d.DeviceId);
            Assert.AreEqual("pc-1", d.MachineName);
            Assert.AreEqual("u1", d.UserName);
            CollectionAssert.AreEqual(new[] { "SessionStart", "Reboot" }, d.Methods!.ToArray());
        }

        // 设备端若用 PascalCase 拼键名也应能解析（context 开了 PropertyNameCaseInsensitive）。
        [TestMethod]
        public void Parse_PascalCaseJson_ReadsAllFields()
        {
            var d = DeviceInfo.Parse("""{"DeviceId":"d1","MachineName":"pc-1","Methods":["Reboot"]}""");

            Assert.AreEqual("d1", d.DeviceId);
            Assert.AreEqual("pc-1", d.MachineName);
            CollectionAssert.AreEqual(new[] { "Reboot" }, d.Methods!.ToArray());
        }

        [TestMethod]
        public void Parse_OnlyDeviceId_LeavesOptionalsNull()
        {
            var d = DeviceInfo.Parse("""{"deviceId":"d1"}""");

            Assert.AreEqual("d1", d.DeviceId);
            Assert.IsNull(d.MachineName);
            Assert.IsNull(d.UserName);
            Assert.IsNull(d.Methods, "未上报方法时应为 null（ChatHub 会当成空列表处理）");
        }

        [TestMethod]
        public void Parse_EmptyMethods_IsEmptyList()
        {
            var d = DeviceInfo.Parse("""{"deviceId":"d1","methods":[]}""");

            Assert.IsNotNull(d.Methods);
            Assert.AreEqual(0, d.Methods!.Count);
        }

        [TestMethod]
        public void Parse_NotJson_Throws()
        {
            Assert.ThrowsException<Exception>(() => DeviceInfo.Parse("not-json"));
        }

        [TestMethod]
        public void Parse_MissingDeviceId_Throws()
        {
            Assert.ThrowsException<Exception>(() => DeviceInfo.Parse("""{"machineName":"pc-1"}"""));
        }

        [TestMethod]
        public void Parse_BlankDeviceId_Throws()
        {
            Assert.ThrowsException<Exception>(() => DeviceInfo.Parse("""{"deviceId":"   "}"""));
        }
    }
}
