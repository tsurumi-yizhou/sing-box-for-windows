using Microsoft.Windows.ApplicationModel.Resources;
using Windows.Globalization;

namespace SFW.Services;

/// <summary>
/// The single accessor for localized user-visible text. Every string lives in
/// Strings/&lt;language&gt;/Resources.resw; XAML reaches them through x:Uid and
/// code-behind through this type, so both sides resolve text through the same
/// resource system and can never drift into different languages.
/// </summary>
public static class Loc
{
    private static ResourceLoader? _loader;

    private static ResourceLoader Loader
    {
        get
        {
            if (_loader is not null) return _loader;

            // MRT Core resolves a .resw name such as "Home.GetStarted" through the
            // resource path "Home/GetStarted" (a dot is a path separator in the
            // package resource index). The loader below applies that mapping; the
            // packaged app always ships resources.pri next to the executable.
            var pri = Path.Combine(AppContext.BaseDirectory, "resources.pri");
            _loader = File.Exists(pri) ? new ResourceLoader(pri) : new ResourceLoader();
            return _loader;
        }
    }

    /// <summary>
    /// The language the resource system resolves for this process, in ranked
    /// order. Windows derives it from the user's language list, which is exactly
    /// what x:Uid lookups use; anything that needs "the app's language" (the
    /// daemon accept-language header, the zh-Hans/zh-Hant choice) must read this
    /// instead of CultureInfo, whose culture is the unrelated regional format.
    /// </summary>
    public static string UiLanguage
    {
        get
        {
            // The user-selected language wins over the Windows list; it is what the
            // resource system resolves x:Uid and Loc.Get against.
            var forced = ApplicationLanguages.PrimaryLanguageOverride;
            if (!string.IsNullOrEmpty(forced)) return forced;
            return ApplicationLanguages.Languages.FirstOrDefault() ?? "en-US";
        }
    }

    public static bool IsZh =>
        UiLanguage.StartsWith("zh", StringComparison.OrdinalIgnoreCase);

    /// <summary>
    /// Resolves a resource key such as "Home.GetStarted". A key missing from the
    /// resolved language falls back to the neutral language inside the resource
    /// system; a key missing everywhere degrades to the key itself instead of
    /// throwing on the UI thread.
    /// </summary>
    public static string Get(string key)
    {
        try
        {
            return Loader.GetString(key.Replace('.', '/'));
        }
        catch (Exception)
        {
            return key;
        }
    }
}
