
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;

public class TaskEx
{
    // example of implementation, actual implementation would probably be much more elaborated 
    // with actual internal logic that returns the tasks a they complete, kinda like WhenAny 
    // already does but as an asynchronous enumeration
    // There could also be a ordered version if someone has the use-case of needing the tasks to be executed sequentially.
    public static async IAsyncEnumerable<T> WhenEach<T>(Task<T>[] source)
    {
        var tasks = source.ToList();

        while (tasks.Count > 0)
        {
            var task = await Task.WhenAny(tasks);
            tasks.Remove(task);
            yield return await task;
        }
    }
}