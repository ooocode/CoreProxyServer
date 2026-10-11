using System.Runtime.InteropServices;
using System.Runtime.InteropServices.Marshalling;

namespace CoreProxy.Com.Impl
{
    [GeneratedComInterface]
    [InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    [Guid(IID)]
    public partial interface IClassFactory
    {
        public const string IID = "00000001-0000-0000-C000-000000000046";

        void CreateInstance(nint pUnkOuter, Guid riid, out nint ppvObject);

        void LockServer([MarshalAs(UnmanagedType.Bool)] bool fLock);
    }
}
