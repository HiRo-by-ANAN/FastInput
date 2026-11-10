using System.Diagnostics;

namespace FastInput.RealtimeTests;

internal sealed class TestSuite
{
    private readonly List<(string Name, Func<Task> Run)> _tests = [];
    public void Add(string name, Action run) => Add(name, () => { run(); return Task.CompletedTask; });
    public void Add(string name, Func<Task> run) => _tests.Add((name, run));

    public async Task<int> RunAsync()
    {
        var total = Stopwatch.StartNew();
        var failures = 0;
        foreach (var (name, run) in _tests)
        {
            var elapsed = Stopwatch.StartNew();
            try
            {
                await run().WaitAsync(TimeSpan.FromSeconds(45));
                Console.WriteLine($"PASS {name} {elapsed.Elapsed.TotalMilliseconds:F1} ms");
            }
            catch (Exception error)
            {
                failures++;
                // Assertions contain only fixed test descriptions, never protocol payloads or credentials.
                Console.WriteLine($"FAIL {name} {elapsed.Elapsed.TotalMilliseconds:F1} ms {error.GetType().Name} {error.Message}");
            }
        }
        Console.WriteLine($"TOTAL passed={_tests.Count - failures} failed={failures} elapsed_ms={total.Elapsed.TotalMilliseconds:F1}");
        return failures == 0 ? 0 : 1;
    }
}

internal static class Check
{
    public static void True(bool condition, string description = "assertion failed")
    {
        if (!condition) throw new InvalidOperationException(description);
    }

    public static void Equal<T>(T expected, T actual, string description = "values differ") =>
        True(EqualityComparer<T>.Default.Equals(expected, actual), description);

    public static void Throws(Action action, string description = "expected rejection")
    {
        try { action(); }
        catch { return; }
        throw new InvalidOperationException(description);
    }

    public static async Task EventuallyAsync(Func<bool> condition, string description, int timeoutMilliseconds = 3000)
    {
        var elapsed = Stopwatch.StartNew();
        while (!condition())
        {
            if (elapsed.ElapsedMilliseconds >= timeoutMilliseconds) throw new InvalidOperationException(description);
            await Task.Delay(5);
        }
    }
}
