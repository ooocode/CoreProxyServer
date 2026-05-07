namespace CoreProxy.Server.Orleans.Test
{
    public class TestClass
    {
        public static async Task RunAsync()
        {
            await foreach (var item in AsyncEnumerableEx.Merge(GetIntsAsync(), GetIntsAAAAAsync()))
            {
                Console.WriteLine(item);
            }

            Console.WriteLine("结束");
        }

        private static async IAsyncEnumerable<int> GetIntsAsync()
        {
            for (int i = 0; i < 10; i++)
            {
                await Task.Delay(1000);
                yield return i;
            }
        }

        private static async IAsyncEnumerable<int> GetIntsAAAAAsync()
        {
            for (int i = 0; i < 100; i++)
            {
                await Task.Delay(1000);
                yield return i;
            }
        }
    }
}
