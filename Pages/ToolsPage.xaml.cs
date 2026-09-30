using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using SFW.Services;
using SFW.Services.Core;

namespace SFW.Pages;

public sealed partial class ToolsPage : Page
{
    private CancellationTokenSource? _nqCts;
    private CancellationTokenSource? _stunCts;

    public ToolsPage()
    {
        InitializeComponent();
        Loaded += OnLoaded;
        Unloaded += OnUnloaded;
    }

    private void OnLoaded(object sender, RoutedEventArgs e)
    {
        App.State.Core.StateChanged += Core_StateChanged;
        UpdateAvailability();
    }

    private void OnUnloaded(object sender, RoutedEventArgs e)
    {
        App.State.Core.StateChanged -= Core_StateChanged;
        _nqCts?.Cancel();
        _stunCts?.Cancel();
    }

    private void Core_StateChanged(object? sender, RuntimeState state) => UpdateAvailability();

    private void UpdateAvailability()
    {
        var core = App.State.Core;
        var libboxAvailable = core is BoxddCoreController;
        var running = core.State.IsRunning;

        StatusBar.IsOpen = !(libboxAvailable && running);
        StatusBar.Severity = InfoBarSeverity.Informational;
        StatusBar.Title = Loc.Get("Tools", "工具");
        StatusBar.Message = !libboxAvailable
            ? Loc.Get("These tools require the libbox core; the app is currently using the development fallback process.",
                      "这些工具需要 libbox 核心；当前正在使用开发后备进程。")
            : Loc.Get("Start the service to use the network quality and STUN tests.",
                      "启动服务后即可使用网络质量和 STUN 测试。");

        NqStartButton.IsEnabled = libboxAvailable && running && _nqCts is null;
        StunStartButton.IsEnabled = libboxAvailable && running && _stunCts is null;

        _ = RefreshVersionAsync();
    }

    private async Task RefreshVersionAsync()
    {
        // SFA/SFM parity: report the core this app ships. The daemon answers its own
        // version without elevation and without a running service, so the card is
        // never blank.
        CoreVersionText.Text = await DaemonServiceManager.QueryVersionAsync()
            ?? Loc.Get("Unavailable", "不可用");
    }

    // ----- Network quality -----

    private async void NqStartButton_Click(object sender, RoutedEventArgs e)
    {
        _nqCts?.Cancel();
        _nqCts = new CancellationTokenSource();
        var token = _nqCts.Token;

        NqStartButton.IsEnabled = false;
        NqProgress.Visibility = Visibility.Visible;
        NqProgress.IsActive = true;
        NqResults.Visibility = Visibility.Collapsed;
        NqPhaseText.Text = string.Empty;

        try
        {
            var options = new CoreNetworkQualityTestOptions(
                string.IsNullOrWhiteSpace(NqConfigUrlBox.Text) ? null : NqConfigUrlBox.Text.Trim(),
                null, Serial: false, MaxRuntimeSeconds: 0, NqHttp3Box.IsChecked == true);

            CoreNetworkQualityProgress? last = null;
            await foreach (var progress in App.State.Core.StartNetworkQualityTestAsync(options, token))
            {
                last = progress;
                NqPhaseText.Text = NqPhaseName(progress.Phase, progress.ElapsedMs);
                UpdateNqResults(progress);
            }
            if (last is { Error: { } error })
            {
                ShowError(error);
            }
            else
            {
                NqPhaseText.Text = Loc.Get("Done", "完成");
            }
        }
        catch (OperationCanceledException) { }
        catch (Exception error)
        {
            ShowError(error.Message);
        }
        finally
        {
            NqProgress.IsActive = false;
            NqProgress.Visibility = Visibility.Collapsed;
            _nqCts = null;
            NqStartButton.IsEnabled = true;
        }
    }

    private void UpdateNqResults(CoreNetworkQualityProgress p)
    {
        if (p.DownloadCapacity <= 0 && p.UploadCapacity <= 0 && p.IdleLatencyMs <= 0) return;
        NqResults.Visibility = Visibility.Visible;
        NqDownloadText.Text = p.DownloadCapacity > 0 ? $"{p.DownloadCapacity * 8.0 / 1_000_000:F1} Mbps" : "…";
        NqUploadText.Text = p.UploadCapacity > 0 ? $"{p.UploadCapacity * 8.0 / 1_000_000:F1} Mbps" : "…";
        var rpm = new List<string>();
        if (p.DownloadRpm > 0) rpm.Add($"↓ {p.DownloadRpm}");
        if (p.UploadRpm > 0) rpm.Add($"↑ {p.UploadRpm}");
        NqRpmText.Text = rpm.Count > 0 ? string.Join("  ", rpm) : "…";
        NqIdleLatencyText.Text = p.IdleLatencyMs > 0 ? $"{p.IdleLatencyMs} ms" : "…";
    }

    private static string NqPhaseName(int phase, long elapsedMs) => phase switch
    {
        0 => Loc.Get("Measuring idle latency…", "正在测量空闲延迟…"),
        1 => string.Format(Loc.Get("Download test… ({0:F0}s)", "下载测试…（{0:F0} 秒）"), elapsedMs / 1000.0),
        2 => string.Format(Loc.Get("Upload test… ({0:F0}s)", "上传测试…（{0:F0} 秒）"), elapsedMs / 1000.0),
        _ => Loc.Get("Done", "完成"),
    };

    // ----- STUN -----

    private async void StunStartButton_Click(object sender, RoutedEventArgs e)
    {
        _stunCts?.Cancel();
        _stunCts = new CancellationTokenSource();
        var token = _stunCts.Token;

        StunStartButton.IsEnabled = false;
        StunProgress.Visibility = Visibility.Visible;
        StunProgress.IsActive = true;
        StunResults.Visibility = Visibility.Collapsed;
        StunPhaseText.Text = string.Empty;

        try
        {
            var server = string.IsNullOrWhiteSpace(StunServerBox.Text) ? string.Empty : StunServerBox.Text.Trim();
            CoreStunProgress? last = null;
            await foreach (var progress in App.State.Core.StartStunTestAsync(server, null, token))
            {
                last = progress;
                StunPhaseText.Text = StunPhaseName(progress.Phase);
                UpdateStunResults(progress);
            }
            if (last is { Error: { } error })
            {
                ShowError(error);
            }
            else
            {
                StunPhaseText.Text = Loc.Get("Done", "完成");
            }
        }
        catch (OperationCanceledException) { }
        catch (Exception error)
        {
            ShowError(error.Message);
        }
        finally
        {
            StunProgress.IsActive = false;
            StunProgress.Visibility = Visibility.Collapsed;
            _stunCts = null;
            StunStartButton.IsEnabled = true;
        }
    }

    private void UpdateStunResults(CoreStunProgress p)
    {
        if (string.IsNullOrEmpty(p.ExternalAddr) && p.LatencyMs <= 0 && p.NatMapping <= 0 && p.NatFiltering <= 0) return;
        StunResults.Visibility = Visibility.Visible;
        if (!string.IsNullOrEmpty(p.ExternalAddr)) StunExternalText.Text = p.ExternalAddr;
        if (p.LatencyMs > 0) StunLatencyText.Text = $"{p.LatencyMs} ms";
        if (p.NatMapping > 0) StunMappingText.Text = NatBehaviorName(p.NatMapping);
        if (p.NatFiltering > 0) StunFilteringText.Text = NatBehaviorName(p.NatFiltering);
    }

    private static string StunPhaseName(int phase) => phase switch
    {
        0 => Loc.Get("Binding…", "正在绑定…"),
        1 => Loc.Get("Probing NAT mapping…", "正在探测 NAT 映射行为…"),
        2 => Loc.Get("Probing NAT filtering…", "正在探测 NAT 过滤行为…"),
        _ => Loc.Get("Done", "完成"),
    };

    private static string NatBehaviorName(int value) => value switch
    {
        1 => Loc.Get("Endpoint Independent", "端点无关"),
        2 => Loc.Get("Address Dependent", "地址相关"),
        3 => Loc.Get("Address and Port Dependent", "地址和端口相关"),
        _ => Loc.Get("Unknown", "未知"),
    };

    private void ShowError(string message)
    {
        StatusBar.IsOpen = true;
        StatusBar.Severity = InfoBarSeverity.Error;
        StatusBar.Title = Loc.Get("Tool error", "工具错误");
        StatusBar.Message = message;
    }
}
