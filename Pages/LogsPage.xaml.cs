using System.Text;
using System.Text.RegularExpressions;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using Windows.ApplicationModel.DataTransfer;
using Windows.Storage.Pickers;
using SFW.Services;

namespace SFW.Pages;

public sealed partial class LogsPage : Page
{
    // SFA LogLevel priorities: lower is more severe; Default shows everything.
    private const int LevelDefault = 7;
    private static readonly Regex LevelPattern = new(
        @"\b(PANIC|FATAL|ERROR|WARN(?:ING)?|INFO|DEBUG|TRACE)\b",
        RegexOptions.IgnoreCase | RegexOptions.Compiled);

    private int _levelFilter = LevelDefault;
    private bool _paused;

    // Incremental rendering: new lines are queued on the producer thread and
    // appended in one batched UI flush instead of rebuilding the whole list.
    private readonly System.Collections.ObjectModel.ObservableCollection<LogRow> _rows = new();
    private readonly List<string> _pending = new();
    private bool _flushScheduled;
    private bool _resetPending;

    public LogsPage()
    {
        InitializeComponent();
        LogList.ItemsSource = _rows;
        Loaded += OnLoaded;
        Unloaded += OnUnloaded;
    }

    private void OnLoaded(object sender, RoutedEventArgs e)
    {
        App.State.LogAdded += OnLogAdded;
        App.State.LogsCleared += OnLogsCleared;
        RebuildAll();
    }

    private void OnUnloaded(object sender, RoutedEventArgs e)
    {
        App.State.LogAdded -= OnLogAdded;
        App.State.LogsCleared -= OnLogsCleared;
    }

    private void OnLogsCleared(object? sender, EventArgs e)
    {
        lock (_pending)
        {
            _pending.Clear();
            _resetPending = true;
        }
        ScheduleFlush();
    }

    private void OnLogAdded(object? sender, string line)
    {
        lock (_pending)
        {
            _pending.Add(line);
            if (_pending.Count > 3000) _pending.RemoveRange(0, _pending.Count - 3000);
        }
        ScheduleFlush();
    }

    private void ScheduleFlush()
    {
        if (_paused) return;
        lock (_pending)
        {
            if (_flushScheduled) return;
            _flushScheduled = true;
        }
        DispatcherQueue.TryEnqueue(FlushPending);
    }

    private void FlushPending()
    {
        string[] lines;
        bool reset;
        lock (_pending)
        {
            _flushScheduled = false;
            if (_paused || (_pending.Count == 0 && !_resetPending)) return;
            reset = _resetPending;
            _resetPending = false;
            lines = _pending.ToArray();
            _pending.Clear();
        }
        if (_paused) return;
        if (reset) _rows.Clear();

        var scrollViewer = VisualTree.FindScrollViewer(LogList);
        var wasAtBottom = scrollViewer is null || scrollViewer.VerticalOffset >= scrollViewer.ScrollableHeight - 4;

        foreach (var line in lines)
        {
            if (!PassesFilter(line)) continue;
            _rows.Add(new LogRow(line, BrushFor(ParseLevel(line))));
        }
        while (_rows.Count > 3000) _rows.RemoveAt(0);

        if (wasAtBottom && _rows.Count > 0)
        {
            LogList.ScrollIntoView(_rows[^1]);
        }
    }

    private bool PassesFilter(string line)
    {
        if (ParseLevel(line) > _levelFilter) return false;
        var search = FilterBox.Text.Trim();
        return string.IsNullOrEmpty(search) || line.Contains(search, StringComparison.OrdinalIgnoreCase);
    }

    // ----- Toolbar -----

    private void PauseButton_Click(object sender, RoutedEventArgs e)
    {
        _paused = !_paused;
        PauseIcon.Glyph = _paused ? "\uE768" : "\uE769"; // Play / Pause
        PauseText.Text = _paused ? Loc.Get("Resume", "继续") : Loc.Get("Pause", "暂停");
        if (!_paused) FlushPending();
    }

    private void LevelMenu_Opening(object? sender, object e)
    {
        foreach (var item in LevelMenu.Items.OfType<MenuFlyoutItem>())
        {
            var selected = item.Tag?.ToString() == _levelFilter.ToString();
            item.Icon = selected ? new FontIcon { Glyph = "\uE73E" } : null;
        }
    }

    private void LevelItem_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not MenuFlyoutItem { Tag: string tag } item) return;
        _levelFilter = int.Parse(tag);
        LevelText.Text = _levelFilter == LevelDefault
            ? Loc.Get("Default", "默认")
            : item.Text;

        // SFA: an active level filter shows a dismissible banner.
        LevelFilterBar.IsOpen = _levelFilter != LevelDefault;
        LevelFilterBar.Title = string.Format(Loc.Get("Filter: {0}", "过滤：{0}"), LevelText.Text);
        RebuildAll();
    }

    private void ClearFilter_Click(object sender, RoutedEventArgs e)
    {
        _levelFilter = LevelDefault;
        LevelText.Text = Loc.Get("Default", "默认");
        LevelFilterBar.IsOpen = false;
        RebuildAll();
    }

    private void FilterBox_TextChanged(object sender, TextChangedEventArgs e) => RebuildAll();

    private void SaveToClipboard_Click(object sender, RoutedEventArgs e)
    {
        var package = new DataPackage();
        package.SetText(string.Join(Environment.NewLine, FilteredLines()));
        Clipboard.SetContent(package);
    }

    private async void SaveToFile_Click(object sender, RoutedEventArgs e)
    {
        var picker = new FileSavePicker
        {
            SuggestedFileName = $"logs_{DateTime.Now:yyyyMMdd_HHmmss}",
            DefaultFileExtension = ".txt",
        };
        picker.FileTypeChoices.Add("Text", [".txt"]);
        if (MainWindow.Instance is not null)
        {
            var hwnd = WinRT.Interop.WindowNative.GetWindowHandle(MainWindow.Instance);
            WinRT.Interop.InitializeWithWindow.Initialize(picker, hwnd);
        }

        var file = await picker.PickSaveFileAsync();
        if (file is null) return;
        await File.WriteAllTextAsync(file.Path, string.Join(Environment.NewLine, FilteredLines()), Encoding.UTF8);
    }

    private async void ClearButton_Click(object sender, RoutedEventArgs e)
    {
        App.State.ClearLogs();
        try
        {
            await App.State.Core.ClearLogsAsync();
        }
        catch (Exception error)
        {
            App.State.AppendLog(string.Format(Loc.Get("{0}  Unable to clear service logs: {1}", "{0}  无法清空服务日志：{1}"), DateTime.Now.ToString("HH:mm:ss"), error.Message));
        }
        RebuildAll();
    }

    // ----- Rendering -----

    private IEnumerable<string> FilteredLines()
    {
        var search = FilterBox.Text.Trim();
        foreach (var line in App.State.SnapshotLogs())
        {
            if (ParseLevel(line) > _levelFilter) continue;
            if (!string.IsNullOrEmpty(search) && !line.Contains(search, StringComparison.OrdinalIgnoreCase)) continue;
            yield return line;
        }
    }

    private void RebuildAll()
    {
        var wasAtBottom = true;
        var scrollViewer = VisualTree.FindScrollViewer(LogList);
        if (scrollViewer is not null)
        {
            wasAtBottom = scrollViewer.VerticalOffset >= scrollViewer.ScrollableHeight - 4;
        }

        _rows.Clear();
        foreach (var line in FilteredLines())
        {
            _rows.Add(new LogRow(line, BrushFor(ParseLevel(line))));
        }
        if (wasAtBottom && _rows.Count > 0)
        {
            LogList.ScrollIntoView(_rows[^1]);
        }
    }

    private static int ParseLevel(string line)
    {
        var match = LevelPattern.Match(line);
        if (!match.Success) return 4; // info
        return match.Groups[1].Value.ToUpperInvariant() switch
        {
            "PANIC" => 0,
            "FATAL" => 1,
            "ERROR" => 2,
            "WARN" or "WARNING" => 3,
            "DEBUG" => 5,
            "TRACE" => 6,
            _ => 4,
        };
    }

    private static Brush BrushFor(int level)
    {
        var key = level switch
        {
            <= 2 => "SystemFillColorCriticalBrush",
            3 => "SystemFillColorCautionBrush",
            5 => "SystemFillColorAttentionBrush",
            6 => "TextFillColorTertiaryBrush",
            _ => "TextFillColorPrimaryBrush",
        };
        return (Brush)Application.Current.Resources[key];
    }

    private sealed record LogRow(string Text, Brush Brush);
}
