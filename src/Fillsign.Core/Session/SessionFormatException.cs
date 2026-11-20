namespace Fillsign.Core.Session;

/// <summary>The editable record is damaged or not one Fillsign wrote. The document opens as a normal PDF (FR-9.5).</summary>
public sealed class SessionFormatException : Exception
{
    public SessionFormatException()
    {
    }

    public SessionFormatException(string message)
        : base(message)
    {
    }

    public SessionFormatException(string message, Exception innerException)
        : base(message, innerException)
    {
    }
}
