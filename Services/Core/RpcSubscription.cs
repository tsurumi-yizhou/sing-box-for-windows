using Grpc.Core;

namespace SFW.Services.Core;

/// <summary>Desktop StreamStore policy: retry transient failures, never retry
/// unsupported/unauthorized methods, and cancel backoff with the session.</summary>
internal static class RpcSubscription
{
    internal static bool IsTerminal(Exception error) => error is RpcException rpc &&
        rpc.StatusCode is StatusCode.Unimplemented or StatusCode.NotFound or
            StatusCode.Unauthenticated or StatusCode.PermissionDenied;

    internal static async Task RunAsync(
        Func<Action, CancellationToken, Task> subscribe,
        Action<Exception> report,
        CancellationToken token,
        Func<TimeSpan, CancellationToken, Task>? delay = null)
    {
        delay ??= Task.Delay;
        var attempt = 0;
        while (!token.IsCancellationRequested)
        {
            try
            {
                await subscribe(() => { token.ThrowIfCancellationRequested(); attempt = 0; }, token);
            }
            catch (Exception) when (token.IsCancellationRequested) { return; }
            catch (Exception error)
            {
                report(error);
                if (IsTerminal(error)) return;
            }
            try
            {
                await delay(TimeSpan.FromSeconds(Math.Min(++attempt, 5)), token);
            }
            catch (OperationCanceledException) when (token.IsCancellationRequested) { return; }
        }
    }
}
