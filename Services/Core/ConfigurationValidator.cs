using System.Diagnostics;
using Desktop;

namespace SFW.Services.Core;

/// <summary>Use boxdd's unprivileged ApplicationService, including when the
/// daemon service is stopped. Never start a VPN merely to validate a file.</summary>
internal static class ConfigurationValidator
{
    private static readonly SemaphoreSlim Gate = new(1, 1);

    public static async Task CheckAsync(string content, CancellationToken token = default)
    {
        await Gate.WaitAsync(token);
        try { await CheckCoreAsync(content, token); }
        finally { Gate.Release(); }
    }

    private static async Task CheckCoreAsync(string content, CancellationToken token)
    {
        var executable = DaemonServiceManager.FindDaemonExecutable()
            ?? throw new InvalidOperationException("sing-box-daemon.exe was not found.");
        var pipe = $"sing-box-worker.{Guid.NewGuid():N}";
        var start = new ProcessStartInfo(executable)
        {
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardInput = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
        };
        foreach (var argument in new[] { "worker", "--socket", $@"\\.\pipe\{pipe}", "--parent-pid", Environment.ProcessId.ToString(),
                     "--daemon-relay-socket", $@"\\.\pipe\sing-box-worker.{Guid.NewGuid():N}" })
            start.ArgumentList.Add(argument);
        using var process = Process.Start(start)
            ?? throw new InvalidOperationException("Unable to start the configuration checker.");
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(token);
        timeout.CancelAfter(TimeSpan.FromSeconds(30));
        var errors = process.StandardError.ReadToEndAsync();
        try
        {
            var ready = await process.StandardOutput.ReadLineAsync(timeout.Token);
            if (ready != "READY")
            {
                var detail = process.HasExited ? await errors : "Worker readiness handshake failed.";
                throw new InvalidOperationException($"Unable to check configuration: {detail.Trim()}");
            }
            using var channel = NamedPipeChannel.Create(pipe);
            var client = new ApplicationService.ApplicationServiceClient(channel);
            await client.CheckConfigAsync(new ConfigContent { Content = content }, cancellationToken: timeout.Token);
        }
        finally
        {
            process.StandardInput.Close();
            using var cleanupTimeout = new CancellationTokenSource(TimeSpan.FromSeconds(2));
            try { await process.WaitForExitAsync(cleanupTimeout.Token); }
            catch (OperationCanceledException)
            {
                if (!process.HasExited) process.Kill(entireProcessTree: true);
                await process.WaitForExitAsync();
            }
            await errors;
        }
    }
}
