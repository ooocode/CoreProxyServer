using System.Diagnostics;

namespace CoreProxy.Internal
{
    public class StopWatchHelper : IDisposable
    {
        private readonly Stopwatch stopwatch = Stopwatch.StartNew();

        public void Dispose()
        {
            stopwatch.Stop();
            Console.WriteLine($"用时 {stopwatch.ElapsedMilliseconds} 毫秒");
            GC.SuppressFinalize(this);
        }
    }
}