using System.Diagnostics;
using Daemon;
using Desktop;
using Google.Protobuf.WellKnownTypes;

namespace SFW.Services.Core;

/// <summary>
/// Opens a short-lived authenticated worker relay to the boxdd daemon. The
/// daemon only accepts a signed worker whose parent is the installed
/// application, so the app cannot talk to DesktopService directly; this helper
/// spawns that worker (the same contract the core controller uses), claims the
/// service when nobody owns it, runs one call, and tears the worker down.
/// </summary>
internal static class DaemonSession
{
    private static readonly SemaphoreSlim Gate = new(1, 1);

    public static async Task<T> RunAsync<T>(
        Func<DesktopService.DesktopServiceClient, ManagedService.ManagedServiceClient, CancellationToken, Task<T>> action,
        CancellationToken cancellationToken)
    {
        await Gate.WaitAsync(cancellationToken);
        try
        {
            if (!await DaemonServiceManager.IsDaemonReachableAsync(cancellationToken))
            {
                throw new InvalidOperationException(Loc.Get("Core.TheSingBoxDaemonServiceIsNotRunningSoThisToolIsUnava"));
            }
            var executable = DaemonServiceManager.FindWorkerExecutable()
                ?? throw new InvalidOperationException("sing-box-daemon.exe was not found.");
            var socketName = $"sing-box-worker.{Guid.NewGuid():N}";
            var relayName = $"sing-box-worker.{Guid.NewGuid():N}";
            var startInfo = new ProcessStartInfo
            {
                FileName = executable,
                UseShellExecute = false,
                CreateNoWindow = true,
                RedirectStandardInput = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
            };
            // boxdd validates this exact argument order before accepting the
            // worker as a relay peer (same contract as the core controller).
            startInfo.ArgumentList.Add("worker");
            startInfo.ArgumentList.Add("--socket");
            startInfo.ArgumentList.Add($@"\\.\pipe\{socketName}");
            startInfo.ArgumentList.Add("--parent-pid");
            startInfo.ArgumentList.Add(Environment.ProcessId.ToString());
            startInfo.ArgumentList.Add("--daemon-relay-socket");
            startInfo.ArgumentList.Add($@"\\.\pipe\{relayName}");

            using var worker = new Process { StartInfo = startInfo };
            if (!worker.Start())
                throw new InvalidOperationException("Unable to start the SingBox worker process.");

            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            timeout.CancelAfter(TimeSpan.FromSeconds(20));
            try
            {
                var errorTask = worker.StandardError.ReadToEndAsync(timeout.Token);
                var ready = await worker.StandardOutput.ReadLineAsync(timeout.Token);
                if (ready != "READY")
                {
                    var error = errorTask.IsCompletedSuccessfully ? errorTask.Result : null;
                    throw new InvalidOperationException(string.IsNullOrWhiteSpace(error)
                        ? "The SingBox worker did not become ready in time."
                        : $"The SingBox worker failed to start: {error.Trim()}");
                }

                using var channel = NamedPipeChannel.Create(relayName);
                var invoker = new LocaleCallInvoker(channel.CreateCallInvoker());
                var desktop = new DesktopService.DesktopServiceClient(invoker);
                var managed = new ManagedService.ManagedServiceClient(invoker);

                var info = await desktop.GetDaemonInfoAsync(new Empty(), cancellationToken: timeout.Token);
                if (info.Ownership == DaemonOwnership.Other)
                {
                    throw new InvalidOperationException(Loc.Get("Core.TheSingBoxServiceIsOwnedByAnotherWindowsUser"));
                }
                if (info.Ownership == DaemonOwnership.Available)
                    await desktop.ClaimServiceAsync(new Empty(), cancellationToken: timeout.Token);

                return await action(desktop, managed, timeout.Token);
            }
            catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
            {
                // The bounded worker/timeout budget expired, not a page unload:
                // surface it instead of letting the UI swallow a bare cancellation.
                throw new TimeoutException(Loc.Get("Core.TheSingBoxDaemonDidNotAnswerTheRequestInTime"));
            }
            finally
            {
                try { worker.StandardInput.Close(); } catch { /* already exited */ }
                using var cleanup = new CancellationTokenSource(TimeSpan.FromSeconds(3));
                try
                {
                    await worker.WaitForExitAsync(cleanup.Token);
                }
                catch (OperationCanceledException)
                {
                    if (!worker.HasExited) worker.Kill(entireProcessTree: true);
                }
            }
        }
        finally
        {
            Gate.Release();
        }
    }
}
