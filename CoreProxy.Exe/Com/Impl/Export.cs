using CoreProxy.Com.Common;
using CoreProxy.Com.Contract;
using Microsoft.Win32;
using System.Diagnostics;
using System.Reflection;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Runtime.InteropServices.Marshalling;
using System.Text;

namespace CoreProxy.Com.Impl
{
    public static class Export
    {
        private const long SELFREG_E_TYPELIB = 0x8002801C;
        private const string ProgID = "MyApp.CoreProxy";

        private static string? GetCurrentDllFileName()
        {
            var assembly = Assembly.GetExecutingAssembly();
            var assemblyName = assembly.GetName().Name;

            var modules = Process.GetCurrentProcess().Modules.OfType<ProcessModule>();
            var names = modules.Where(x => Path.GetFileNameWithoutExtension(x.FileName) == assemblyName)
                .Select(x => x.FileName).ToArray();
            if (names.Length != 1)
            {
                return null;
            }

            return names[0];
        }

        /// <summary>
        /// 注册服务
        /// </summary>
        /// <returns></returns>
        [UnmanagedCallersOnly(EntryPoint = nameof(DllRegisterServer), CallConvs = [typeof(CallConvCdecl)])]
        private static long DllRegisterServer()
        {
            var currentDllFileName = GetCurrentDllFileName();
            if (currentDllFileName == null)
            {
                return SELFREG_E_TYPELIB;
            }

            var saveDir = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "CoreProxy");
            if (!Directory.Exists(saveDir))
            {
                Directory.CreateDirectory(saveDir);
            }

            foreach (var item in Directory.EnumerateFiles(saveDir))
            {
                try
                {
                    File.Delete(item);
                }
                catch (Exception ex)
                {
                    Console.WriteLine($"Delete file {item} failed: {ex.Message}");
                }
            }

            var saveFileName = Path.Combine(saveDir, $"CoreProxy-{DateTimeOffset.Now:yyyyMMddHHmm}-{Guid.CreateVersion7():N}.dll");
            File.Copy(currentDllFileName, saveFileName, true);

            if (OperatingSystem.IsWindows())
            {
                string keyPath = $"CLSID\\{{{IRun.Clsid}}}\\InprocServer32";
                using RegistryKey key = Registry.ClassesRoot.CreateSubKey(keyPath);
                key.SetValue("", saveFileName);
                key.SetValue("ThreadingModel", "Both");
                key.SetValue("DateTime", DateTimeOffset.Now.ToString());
            }

            return ComConsts.S_OK;
        }

        /// <summary>
        /// 卸载服务
        /// </summary>
        /// <returns></returns>
        [UnmanagedCallersOnly(EntryPoint = nameof(DllUnregisterServer), CallConvs = [typeof(CallConvCdecl)])]
        private static long DllUnregisterServer()
        {
            if (OperatingSystem.IsWindows())
            {
                string keyPath = $"CLSID\\{{{IRun.Clsid}}}";
                using var key = Registry.ClassesRoot.OpenSubKey(keyPath);
                if (key != null)
                {
                    Registry.ClassesRoot.DeleteSubKeyTree(keyPath);
                }
            }
            return ComConsts.S_OK;
        }


        private const string IUnknownIID = "00000000-0000-0000-c000-000000000046";

        /// <summary>
        /// 获取类工厂
        /// </summary>
        /// <param name="rclsid"></param>
        /// <param name="riid"></param>
        /// <param name="ppv"></param>
        /// <returns></returns>
        [UnmanagedCallersOnly(EntryPoint = nameof(DllGetClassObject), CallConvs = [typeof(CallConvCdecl)])]
        private static unsafe uint DllGetClassObject(Guid rclsid, Guid riid, nint* ppv)
        {
            StringBuilder log = new();
            log.AppendLine(DateTimeOffset.Now.ToString());
            log.AppendLine("DllGetClassObject rclsid:" + (rclsid).ToString());
            log.AppendLine("DllGetClassObject riid:" + (riid).ToString());
            log.AppendLine();
            var saveDir = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "CoreProxy", "logs");
            if (!Directory.Exists(saveDir))
            {
                Directory.CreateDirectory(saveDir);
            }
            string logFileName = Path.Combine(saveDir, $"{DateTimeOffset.Now:yyyy-MM-dd}.txt");
            File.AppendAllText(logFileName, log.ToString());

            if (IsGuidEquals(riid, IClassFactory.IID) || IsGuidEquals(riid, IUnknownIID))
            {
                object? obj = new ClassFactoryImpl(rclsid);
                ComWrappers cw = new StrategyBasedComWrappers();
                var comInterface = cw.GetOrCreateComInterfaceForObject(obj, CreateComInterfaceFlags.None);

                Marshal.QueryInterface(comInterface, riid, out *ppv);

                return ComConsts.S_OK;
            }
            else
            {
                return ComConsts.CLASS_E_CLASSNOTAVAILABLE;
            }
        }

        private static bool IsGuidEquals(Guid guid, string str)
        {
            // 尝试将字符串转换为 GUID
            if (Guid.TryParse(str, out Guid parsedGuid))
            {
                // 返回比较结果
                return guid.ToString()
                    .Equals(str, StringComparison.OrdinalIgnoreCase);
            }
            return false; // 如果字符串不是有效的 GUID 格式
        }
    }
}