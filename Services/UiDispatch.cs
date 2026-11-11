using Microsoft.UI.Dispatching;
using SFW.Services;

namespace SFW.Services;

/// <summary>
/// Queues UI work on the dispatcher and never lets an exception escape the
/// callback: WinUI turns such an exception into a stowed-exception fail-fast
/// (0xC000027B) instead of raising Application.UnhandledException, which makes
/// the process disappear with no managed crash log. Logging and continuing keeps
/// one broken dashboard update from killing the whole app.
/// </summary>
public static class UiDispatch
{
    public static void Post(this DispatcherQueue queue, Action action, string context)
    {
        queue.TryEnqueue(() =>
        {
            try
            {
                action();
            }
            catch (Exception exception)
            {
                StartupDiag.Log($"UiDispatch: {context} FAILED: {exception}");
            }
        });
    }
}
