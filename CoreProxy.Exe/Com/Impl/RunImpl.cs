using CoreProxy.Com.Common;
using CoreProxy.Com.Contract;
using CoreProxy.ViewModels;
using System.Runtime.InteropServices;
using System.Runtime.InteropServices.Marshalling;

namespace CoreProxy.Com.Impl;

[GeneratedComClass]
[Guid(IRun.Clsid)]
public partial class RunImpl : IRun
{
    private static readonly List<ServerItem> serverItems = [];

    public void AddServerItem(int localListenerPort, string serverHttpAddress, string password, bool enableDataEncrypt)
    {
        /*serverItems.Add(new ServerItem(localListenerPort,
            serverHttpAddress,
            password,
            enableDataEncrypt));*/
    }

    public void Execute(string[] args, int argCount)
    {
        foreach (var item in args)
        {
            Console.WriteLine(item);
        }
        if (serverItems.Count == 0)
        {
            const int hr = unchecked((int)ComConsts.E_INVALIDARG); // E_POINTER
            Marshal.ThrowExceptionForHR(hr);
            return;
        }
        MainFunc.StartMain(serverItems, args.Take(argCount).ToArray());
    }

    //public void OnLog(IRun.OnLogDelegate callback)
    //{
    //    while (true)
    //    {
    //        callback(DateTimeOffset.Now.ToString());
    //        Thread.Sleep(1000);
    //    }
    //}
}