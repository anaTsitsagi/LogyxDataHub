namespace DataHub.Oris;

/// <summary>
/// An ORIS account reference such as <c>"3 1 20 1 193"</c>.
/// </summary>
/// <remarks>
/// ORIS writes the account code as space-separated groups. The first 4 digits form the
/// balance account (<c>3 1 20</c> → <c>3120</c>); every following group is a sub-account of
/// the one before it (<c>1</c>, then <c>193</c> under <c>1</c>).
/// </remarks>
public sealed record OrisAccount(string Code, IReadOnlyList<string> SubAccounts, string Raw)
{
    public const int CodeLength = 4;

    /// <summary>Sub-account path as exposed by the API (<c>debetSub</c>/<c>creditSub</c>), e.g. <c>"1 193"</c>.</summary>
    public string Sub => string.Join(' ', SubAccounts);

    public override string ToString() => SubAccounts.Count == 0 ? Code : $"{Code} {Sub}";

    public static bool TryParse(string? raw, out OrisAccount? account)
    {
        account = null;
        if (string.IsNullOrWhiteSpace(raw)) return false;

        var groups = raw.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        var code = "";
        int i = 0;
        while (i < groups.Length && code.Length < CodeLength)
            code += groups[i++];

        if (code.Length != CodeLength || !code.All(char.IsAsciiDigit)) return false;

        account = new OrisAccount(code, groups[i..], raw.Trim());
        return true;
    }

    public static OrisAccount Parse(string raw) =>
        TryParse(raw, out var account) ? account! : throw new FormatException($"Invalid ORIS account '{raw}'.");
}
