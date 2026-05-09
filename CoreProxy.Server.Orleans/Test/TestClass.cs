namespace CoreProxy.Server.Orleans.Test
{
    public class TestClass
    {
        public static async Task RunAsync()
        {
            try
            {
                var taskClient = GetIntsAAAAAsync();

                var result = await taskClient.WaitAsync(TimeSpan.FromSeconds(5));

                var c = await Task.WhenAny(taskClient);
            }
            catch (Exception ex)
            {
                Console.WriteLine(ex);
            }


            await Task.Delay(-1);
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

        private static async Task<int> GetIntsAAAAAsync(CancellationToken cancellationToken = default)
        {
            for (int i = 0; i < 300; i++)
            {
                if (cancellationToken.IsCancellationRequested)
                {
                    break;
                }
                Console.WriteLine(i);
                await Task.Delay(1000);
            }
            return 100;
        }
    }
}
