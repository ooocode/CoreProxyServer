using System.Diagnostics.CodeAnalysis;
using System.Runtime.InteropServices;
using System.Runtime.InteropServices.Marshalling;

namespace CoreProxy.Com.Contract
{
    [GeneratedComInterface]
    [InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    [Guid(IID)]
    public partial interface IRun
    {
        public const string Clsid = "0000002F-0000-0000-C000-00000000001A";
        public const string IID = "3480CD26-8AEF-4475-B00E-470D1C22B306";

        void AddServerItem(int localListenerPort,
            [MarshalAs(UnmanagedType.BStr)]
            string serverHttpAddress,
            [MarshalAs(UnmanagedType.BStr)]
            string password,
            [MarshalAs(UnmanagedType.Bool)]
            bool enableDataEncrypt = false);

        void Execute(
            [In, MarshalAs(UnmanagedType.LPArray, SizeParamIndex = 1,ArraySubType = UnmanagedType.BStr)]
            string[] args,
            int argCount);

        //[UnmanagedFunctionPointer(CallingConvention.Cdecl)]
        //public delegate void OnLogDelegate([MarshalAs(UnmanagedType.BStr)] string message);

        //void OnLog(OnLogDelegate callback);

        [RequiresUnreferencedCode("This method uses reflection and might break trimming.")]
        public static IRun? CreateObject()
        {
            if (OperatingSystem.IsWindows())
            {
                var type = Type.GetTypeFromCLSID(Guid.Parse(Clsid));
                if (type == null)
                {
                    return null;
                }

                return (IRun?)Activator.CreateInstance(type);
            }

            return null;
        }
    }
}
