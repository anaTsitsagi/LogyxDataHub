using System.Text;

namespace DataHub.Oris;

/// <summary>
/// Converts ORIS legacy 8-bit Georgian text to Unicode.
/// </summary>
/// <remarks>
/// ORIS stores Georgian with a font-based 8-bit layout: bytes from 0xC0 follow the full Georgian
/// alphabet order, including obsolete letters (ჱ ჲ ჳ ჴ ჵ). Unicode's Mkhedruli block orders those
/// letters differently, so a plain offset does not work. Bytes below 0xC0 are passed through
/// unchanged (ASCII, digits, punctuation, Latin text such as "Tbilisi Energy").
/// Input must be read from the file as Latin-1 so each char holds exactly one original byte.
/// </remarks>
public static class GeorgianText
{
    private const int FirstGeorgianByte = 0xC0;
    private const string GeorgianFromC0 = "აბგდევზჱთიკლმნჲოპჟრსტჳუფქღყშჩცძწჭხჴჯჰჵ";

    /// <summary>Latin-1 encoding to use when reading raw ORIS strings.</summary>
    public static Encoding RawEncoding => Encoding.Latin1;

    public static string Decode(string? latin1)
    {
        if (string.IsNullOrEmpty(latin1)) return string.Empty;

        var sb = new StringBuilder(latin1.Length);
        foreach (var ch in latin1)
        {
            int index = ch - FirstGeorgianByte;
            sb.Append(index >= 0 && index < GeorgianFromC0.Length ? GeorgianFromC0[index] : ch);
        }
        return sb.ToString().Trim();
    }
}
