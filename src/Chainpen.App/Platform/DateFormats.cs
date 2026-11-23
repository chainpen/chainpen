using System.Collections.Immutable;
using System.Globalization;
using Chainpen.App.Localization;

namespace Chainpen.App.Platform;

/// <summary>The date formats Settings offers, for the Date tool and the dates Chainpen shows.</summary>
public enum DateFormatChoice
{
    DayMonthYear,
    MonthDayYear,
    YearMonthDay,
    DayMonthYearDots,
    YearMonthDaySlashes,
}

/// <summary>
/// Every date Chainpen writes or shows goes through here, in the format chosen in Settings. The first start picks the
/// one closest to the system's own; forms want a full date, so it's always two-digit days and months and a
/// four-digit year.
/// </summary>
public static class DateFormats
{
    public static readonly ImmutableArray<DateFormatChoice> Choices =
        [DateFormatChoice.DayMonthYear, DateFormatChoice.MonthDayYear, DateFormatChoice.YearMonthDay, DateFormatChoice.YearMonthDaySlashes, DateFormatChoice.DayMonthYearDots];

    private static DateFormatChoice? s_current;

    static DateFormats() => AppSettings.Changed += changed => s_current = changed.DateFormat;

    /// <summary>The chosen format, or the system's before one has been stored.</summary>
    public static DateFormatChoice Current => s_current ??= AppSettings.Load().DateFormat ?? FromCulture(Languages.SystemRegion);

    /// <summary>The format closest to a culture's short date: its order of day, month and year, and dots if it uses them.</summary>
    public static DateFormatChoice FromCulture(CultureInfo culture)
    {
        ArgumentNullException.ThrowIfNull(culture);
        string pattern = culture.DateTimeFormat.ShortDatePattern;
        int day = pattern.IndexOf('d', StringComparison.Ordinal);
        int month = pattern.IndexOf('M', StringComparison.Ordinal);
        int year = pattern.IndexOf('y', StringComparison.Ordinal);
        if (day < 0 || month < 0 || year < 0)
        {
            return DateFormatChoice.DayMonthYear;
        }

        if (year < month && month < day)
        {
            return culture.DateTimeFormat.DateSeparator == "/" ? DateFormatChoice.YearMonthDaySlashes : DateFormatChoice.YearMonthDay;
        }

        if (month < day)
        {
            return DateFormatChoice.MonthDayYear;
        }

        return culture.DateTimeFormat.DateSeparator == "." ? DateFormatChoice.DayMonthYearDots : DateFormatChoice.DayMonthYear;
    }

    /// <summary>"DD/MM/YYYY": how the format is named in Settings and hints.</summary>
    public static string Label(DateFormatChoice choice) => choice switch
    {
        DateFormatChoice.MonthDayYear => Strings.Settings_MMDDYYYY,
        DateFormatChoice.YearMonthDay => Strings.Settings_YYYYMMDD,
        DateFormatChoice.DayMonthYearDots => Strings.Settings_DDMMYYYYDots,
        DateFormatChoice.YearMonthDaySlashes => Strings.Settings_YYYYMMDDSlashes,
        _ => Strings.Settings_DDMMYYYY,
    };

    public static string Format(DateTime date, DateFormatChoice choice) => date.ToString(Pattern(choice), CultureInfo.InvariantCulture);

    /// <summary>A date in the chosen format: what the Date tool writes.</summary>
    public static string Format(DateTime date) => Format(date, Current);

    /// <summary>A moment as local date and time, "21/11/2026, 14:05", for when something was saved or recovered.</summary>
    public static string FormatWithTime(DateTimeOffset when)
    {
        DateTime local = when.ToLocalTime().DateTime;
        return $"{Format(local)}, {local.ToString("HH:mm", CultureInfo.InvariantCulture)}";
    }

    private static string Pattern(DateFormatChoice choice) => choice switch
    {
        DateFormatChoice.MonthDayYear => "MM/dd/yyyy",
        DateFormatChoice.YearMonthDay => "yyyy-MM-dd",
        DateFormatChoice.DayMonthYearDots => "dd.MM.yyyy",
        DateFormatChoice.YearMonthDaySlashes => "yyyy/MM/dd",
        _ => "dd/MM/yyyy",
    };
}
