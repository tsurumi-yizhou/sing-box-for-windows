using Grpc.Core;
using SFW.Services;
using SFW.Services.Core;

namespace SFW.Tests;

internal static class Program
{
    private static void Require(bool condition, string message)
    {
        if (!condition) throw new InvalidOperationException(message);
    }

    private static async Task Main(string[] args)
    {
        await RetryTransientFailures();
        await StopOnTerminalFailures();
        await ReconnectAfterEof();
        await CancelDuringBackoff();
        await RejectLateUpdates();
        await PreserveAtomicFile();
        if (args.Contains("--integration")) await ValidateWithRealWorker();
        Console.WriteLine("PASS: retry/reset/cap, terminal errors, EOF recovery, cancellation, stale updates, atomic writes");
    }

    private static async Task ValidateWithRealWorker()
    {
        await ConfigurationValidator.CheckAsync("{\"outbounds\":[{\"type\":\"direct\",\"tag\":\"direct\"}]}");
        try
        {
            await ConfigurationValidator.CheckAsync("{\"outbounds\":[{\"type\":\"invalid-protocol\"}]}");
            throw new Exception("The real core must reject a syntactically valid but unsupported configuration.");
        }
        catch (RpcException) { }
        // A failed check must not poison the next worker/session.
        await ConfigurationValidator.CheckAsync("{}");
        Console.WriteLine("PASS: signed worker accepts valid config, rejects invalid protocol, and recovers after failure");
    }

    private static async Task RetryTransientFailures()
    {
        using var stop = new CancellationTokenSource();
        var delays = new List<double>();
        var calls = 0;
        var reports = 0;
        await RpcSubscription.RunAsync((received, _) =>
        {
            calls++;
            if (calls == 4) received();
            if (calls == 10) { stop.Cancel(); return Task.CompletedTask; }
            throw new RpcException(new Status(StatusCode.Unavailable, "connection lost"));
        }, _ => reports++, stop.Token, (delay, _) =>
        {
            delays.Add(delay.TotalSeconds);
            return Task.CompletedTask;
        });
        Require(delays.Take(9).SequenceEqual(new double[] { 1, 2, 3, 1, 2, 3, 4, 5, 5 }), "Backoff must reset after a message and cap at 5 seconds.");
        Require(calls == 10 && reports == 9, "Transient failures must reconnect.");
    }

    private static async Task StopOnTerminalFailures()
    {
        foreach (var code in new[] { StatusCode.Unimplemented, StatusCode.NotFound, StatusCode.Unauthenticated, StatusCode.PermissionDenied })
        {
            var calls = 0;
            await RpcSubscription.RunAsync((_, _) =>
            {
                calls++;
                throw new RpcException(new Status(code, "terminal"));
            }, _ => { }, CancellationToken.None, (_, _) => throw new Exception("Terminal failures must not back off."));
            Require(calls == 1, "Terminal failures must not retry.");
        }
    }

    private static async Task ReconnectAfterEof()
    {
        using var stop = new CancellationTokenSource();
        var calls = 0;
        await RpcSubscription.RunAsync((_, _) =>
        {
            if (++calls == 2) stop.Cancel();
            return Task.CompletedTask;
        }, _ => throw new Exception("EOF is not an RPC error."), stop.Token, (_, _) => Task.CompletedTask);
        Require(calls == 2, "Unexpected EOF must reconnect.");
    }

    private static async Task CancelDuringBackoff()
    {
        using var stop = new CancellationTokenSource();
        var waiting = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var calls = 0;
        var task = RpcSubscription.RunAsync((_, _) =>
        {
            calls++;
            throw new IOException("offline");
        }, _ => { }, stop.Token, (_, token) =>
        {
            waiting.SetResult();
            return Task.Delay(Timeout.InfiniteTimeSpan, token);
        });
        await waiting.Task;
        stop.Cancel();
        await task.WaitAsync(TimeSpan.FromSeconds(2));
        Require(calls == 1, "Stopping during backoff must not resubscribe.");
    }

    private static async Task RejectLateUpdates()
    {
        using var stop = new CancellationTokenSource();
        var applied = false;
        await RpcSubscription.RunAsync((received, _) =>
        {
            stop.Cancel();
            received();
            applied = true;
            return Task.CompletedTask;
        }, _ => { }, stop.Token);
        Require(!applied, "A canceled session must not apply late messages.");
    }

    private static async Task PreserveAtomicFile()
    {
        var directory = Path.Combine(Path.GetTempPath(), "sfw-atomic-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        try
        {
            var path = Path.Combine(directory, "config.json");
            AtomicFile.WriteAllText(path, "old");
            using var stop = new CancellationTokenSource();
            stop.Cancel();
            try
            {
                await AtomicFile.WriteAllTextAsync(path, "new", stop.Token);
                throw new Exception("Canceled writes must fail.");
            }
            catch (OperationCanceledException) { }
            Require(File.ReadAllText(path) == "old", "Canceled writes must preserve the previous file.");
            await AtomicFile.WriteAllTextAsync(path, "配置");
            Require(File.ReadAllText(path) == "配置", "Successful writes must replace the previous content.");
            Require(Directory.GetFiles(directory).Length == 1, "Temporary files must be cleaned up.");
        }
        finally
        {
            foreach (var file in Directory.GetFiles(directory)) File.Delete(file);
            Directory.Delete(directory);
        }
    }
}
