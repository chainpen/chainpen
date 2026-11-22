using System.Collections.Immutable;
using System.Diagnostics.CodeAnalysis;
using System.Globalization;

namespace Fillsign.App.Localization;

/// <summary>A language Fillsign speaks: its culture code and its own name for itself, as Settings lists it.</summary>
public sealed record AppLanguage(string Code, string Name);

/// <summary>
/// Which language the interface uses: the one chosen in Settings, else the computer's if Fillsign has it, else
/// English. Set at start-up before any text is shown, and again when Settings changes it, which rebuilds the open
/// windows in the new language (<see cref="Changed"/>).
/// </summary>
public static class Languages
{
    public static readonly ImmutableArray<AppLanguage> Supported =
    [
        new("en", "English"),
        new("de", "Deutsch"),
        new("ja", "日本語"),
    ];

    // The computer's own language and region, kept before Fillsign sets its own so Settings can still say what
    // "System" means.
    private static readonly CultureInfo s_system = CultureInfo.CurrentUICulture;

    /// <summary>
    /// The computer's own region, as it was before Fillsign set its language: what the date format starts from, since
    /// a French computer running Fillsign in English still writes dates the French way.
    /// </summary>
    public static CultureInfo SystemRegion { get; } = CultureInfo.CurrentCulture;

    /// <summary>The language this run of Fillsign uses ("de"), set by <see cref="Apply"/>.</summary>
    public static string Running { get; private set; } = "en";

    /// <summary>The language the computer asks for, if Fillsign has it, else English.</summary>
    public static AppLanguage FromSystem() => FromCulture(s_system);

    public static AppLanguage FromCulture(CultureInfo culture)
    {
        ArgumentNullException.ThrowIfNull(culture);
        return Supported.FirstOrDefault(language => language.Code == culture.TwoLetterISOLanguageName) ?? Supported[0];
    }

    /// <summary>Whether <paramref name="code"/> is one of Fillsign's languages ("de"), as stored in settings.</summary>
    public static bool IsSupported([NotNullWhen(true)] string? code) => Supported.Any(language => language.Code == code);

    /// <summary>
    /// Uses <paramref name="chosen"/> (a code from Settings), or the system's language when it's null, for both the
    /// text and how numbers are written in it. The computer's region is kept when it speaks that language (Swiss
    /// German stays Swiss); otherwise the language's usual region.
    /// </summary>
    public static void Apply(string? chosen)
    {
        string code = IsSupported(chosen) ? chosen : FromSystem().Code;
        bool isChange = code != Running;
        Running = code;
        CultureInfo culture = SystemRegion.TwoLetterISOLanguageName == code
            ? SystemRegion
            : CultureInfo.CreateSpecificCulture(code);
        CultureInfo.DefaultThreadCurrentUICulture = culture;
        CultureInfo.DefaultThreadCurrentCulture = culture;
        CultureInfo.CurrentUICulture = culture;
        CultureInfo.CurrentCulture = culture;
        if (isChange)
        {
            Changed?.Invoke();
        }
    }

    /// <summary>Raised after the language changes, so open windows show their text in it straight away.</summary>
    public static event Action? Changed;
}
