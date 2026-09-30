using System.Globalization;

namespace SFW.Services;

public static class Loc
{
    public static bool IsZh => CultureInfo.CurrentCulture.Name.StartsWith("zh", StringComparison.OrdinalIgnoreCase);

    public static string Get(string en, string zh) => IsZh ? zh : en;
}