using System.Globalization;

[assembly: AssemblyFixture(typeof(Fillsign.Pdf.Tests.EnglishCulture))]

namespace Fillsign.Pdf.Tests;

/// <summary>Tests check English messages and labels, whatever language the computer running them uses.</summary>
public sealed class EnglishCulture
{
    public EnglishCulture()
    {
        CultureInfo english = CultureInfo.GetCultureInfo("en");
        CultureInfo.DefaultThreadCurrentUICulture = english;
        CultureInfo.CurrentUICulture = english;
    }
}
