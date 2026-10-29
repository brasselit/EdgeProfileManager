using System.Globalization;

namespace EdgeProfileManager;

/// <summary>UI language: Windows display language, or the one set in the configuration.</summary>
public static class Localization
{
    /// <summary>Languages with a translation; the first one is the fallback of the resources.</summary>
    public static readonly (string Code, string Name)[] Languages = [("en", "English"), ("de", "Deutsch")];

    /// <summary>Must be called before the first window, menu or Jump List is created.</summary>
    public static void Apply(string? language)
    {
        if (string.IsNullOrWhiteSpace(language)) return;
        try
        {
            var culture = CultureInfo.GetCultureInfo(language.Trim());
            CultureInfo.DefaultThreadCurrentUICulture = culture;
            CultureInfo.CurrentUICulture = culture;
        }
        catch (CultureNotFoundException)
        {
            AppLog.Write($"Unknown language '{language}' in the configuration; using the Windows display language.");
        }
    }
}
