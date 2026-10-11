using System.Runtime.InteropServices;
using System.Runtime.InteropServices.Marshalling;

namespace CoreProxy.Com.Contract
{
    public unsafe class DllGetClassObjectClass : IDisposable
    {
        private readonly nint libHandle;

        private delegate* unmanaged[Cdecl]<ref Guid, ref Guid, out nint, int> DllGetClassObjectProcAddress;

        public DllGetClassObjectClass(string dllNameOrPragId)
        {
            if (File.Exists(dllNameOrPragId))
            {
                // 加载原生 DLL
                libHandle = NativeLibrary.Load(dllNameOrPragId);
                return;
            }

            var config = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), $"ProgID-{dllNameOrPragId}", "config.txt");
            if (!File.Exists(config))
            {
                throw new Exception($"找不到配置文件：{config}");
            }

            var dllName = File.ReadAllLines(config).FirstOrDefault(x => x.StartsWith("DllFileName="))?.Split("=").LastOrDefault()?.Trim();
            if (string.IsNullOrWhiteSpace(dllName))
            {
                throw new Exception($"配置文件：{config} 不存在 DllFileName");
            }

            if (!File.Exists(dllName))
            {
                throw new Exception($"找不到原生库：{dllName}");
            }
            libHandle = NativeLibrary.Load(dllName);
        }

        public TInterface DllGetClassObject<TInterface>(string rclsid, string riid)
        {
            // 获取 DllGetClassObject 函数的指针
            nint procAddress = NativeLibrary.GetExport(libHandle, "DllGetClassObject");
            DllGetClassObjectProcAddress = (delegate* unmanaged[Cdecl]<ref Guid, ref Guid, out nint, int>)procAddress;

            // 准备 CLSID 和 IID
            Guid clsid = Guid.Parse(rclsid);
            Guid iid = Guid.Parse(riid);

            // 调用 DllGetClassObject
            int result = DllGetClassObjectProcAddress(ref clsid, ref iid, out var ppv);
            if (result != 0)
            {
                throw new Exception("DllGetClassObject 调用失败，错误码：" + result);
            }

            ComWrappers cw = new StrategyBasedComWrappers();
            var obj = (TInterface)cw.GetOrCreateObjectForComInstance(ppv, CreateObjectFlags.None);
            return obj;
        }

        public void Dispose()
        {
            // 卸载原生库
            NativeLibrary.Free(libHandle);
            GC.SuppressFinalize(this);
        }
    }
}