using System.Globalization;

namespace sing_box_for_windows.Services;

public static class Loc
{
    public static bool IsZh => CultureInfo.CurrentCulture.Name.StartsWith("zh", StringComparison.OrdinalIgnoreCase);

    public static string Get(string en, string zh) => IsZh ? zh : en;
}