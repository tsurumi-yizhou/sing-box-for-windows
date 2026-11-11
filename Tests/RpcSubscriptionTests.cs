using Grpc.Core;
using SFW.Services.Core;
using Xunit;

namespace SFW.Tests;

/// <summary>
/// Stream reconnection policy: the daemon owns long-lived gRPC streams, so a
/// dropped connection must retry with the SFA-compatible backoff while terminal
/// errors and cancellation end the subscription instead.
/// </summary>
public sealed class RpcSubscriptionTests
{
    [Fact]
    public async Task TransientFailuresReconnectWithBoundedBackoff()
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

        Assert.True(delays.Take(9).SequenceEqual(new double[] { 1, 2, 3, 1, 2, 3, 4, 5, 5 }),
            "Backoff must reset after a message and cap at 5 seconds.");
        Assert.True(calls == 10 && reports == 9, "Transient failures must reconnect.");
    }

    [Fact]
    public async Task TerminalFailuresDoNotRetry()
    {
        foreach (var code in new[] { StatusCode.Unimplemented, StatusCode.NotFound, StatusCode.Unauthenticated, StatusCode.PermissionDenied })
        {
            var calls = 0;
            await RpcSubscription.RunAsync((_, _) =>
            {
                calls++;
                throw new RpcException(new Status(code, "terminal"));
            }, _ => { }, CancellationToken.None, (_, _) => throw new Exception("Terminal failures must not back off."));
            Assert.True(calls == 1, "Terminal failures must not retry.");
        }
    }

    [Fact]
    public async Task UnexpectedEofReconnects()
    {
        using var stop = new CancellationTokenSource();
        var calls = 0;
        await RpcSubscription.RunAsync((_, _) =>
        {
            if (++calls == 2) stop.Cancel();
            return Task.CompletedTask;
        }, _ => throw new Exception("EOF is not an RPC error."), stop.Token, (_, _) => Task.CompletedTask);
        Assert.True(calls == 2, "Unexpected EOF must reconnect.");
    }

    [Fact]
    public async Task CancellationDuringBackoffStopsResubscribing()
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
        await task.WaitAsync(TimeSpan.FromSeconds(2), TestContext.Current.CancellationToken);
        Assert.True(calls == 1, "Stopping during backoff must not resubscribe.");
    }

    [Fact]
    public async Task CanceledSessionDoesNotApplyLateMessages()
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
        Assert.False(applied, "A canceled session must not apply late messages.");
    }
}
