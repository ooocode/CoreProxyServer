
public class Test
{
    public static async Task RunAsync()
    {

        CancellationTokenSource cancellationTokenSource = new CancellationTokenSource();
        cancellationTokenSource.CancelAfter(TimeSpan.FromSeconds(10));
        var a = TaskAsync(cancellationTokenSource.Token);
        var b = TaskBAsync(cancellationTokenSource.Token);

        var complete = await Task.WhenAny(a, b);
        if(complete.Id == a.Id)
        {
            Console.WriteLine("aaaaaaaaaaa ok");
        }
        else
        {
            Console.WriteLine("bbbbbbbbbbbbbb ok");
        }

        Console.WriteLine("1 ok");

        await foreach (var item in Task.WhenEach(a, b))
        {
            if (item.Exception is not null)
            {
                Console.WriteLine(item.Exception);
            }
        }


        Console.WriteLine("------");
        Console.ReadLine();
    }

    static async Task TaskAsync(CancellationToken cancellationToken)
    {
        for (var i = 0; i < 500; i++)
        {
            Console.WriteLine(i);
            //await Task.Delay(1000);
            cancellationToken.ThrowIfCancellationRequested();
            throw new Exception("抛出A");
        }
    }

    static async Task TaskBAsync(CancellationToken cancellationToken)
    {
        for (var i = 0; i < 5000; i++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            Console.WriteLine(i);
            await Task.Delay(1000);
        }
    }
}