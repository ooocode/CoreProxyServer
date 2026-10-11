using System.Runtime.InteropServices;
using System.Runtime.InteropServices.Marshalling;

namespace CoreProxy.Com.Impl
{
    [GeneratedComClass]
    public partial class ClassFactoryImpl(Guid rclsid) : IClassFactory
    {
        private readonly Guid rclsid = rclsid;

        public const uint CLASS_E_CLASSNOTAVAILABLE = 0x80040111;

        public void CreateInstance(nint pUnkOuter, Guid riid, out nint ppvObject)
        {
            Console.WriteLine("CreateInstance classId:" + rclsid.ToString());
            Console.WriteLine("CreateInstance riid:" + riid.ToString());

            ComWrappers cw = new StrategyBasedComWrappers();
            object obj = new RunImpl();
            var comInterface = cw.GetOrCreateComInterfaceForObject(obj, CreateComInterfaceFlags.None);

            Marshal.QueryInterface(comInterface, riid, out ppvObject);
        }

        public void LockServer(bool fLock)
        {
            Console.WriteLine("LockServer");
        }
    }
}
