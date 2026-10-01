namespace DataHub.Oris.Tests;

/// <summary>
/// Tests against real ORIS exports. The files contain customer financial data and are never
/// committed; point DATAHUB_ORIS_SAMPLES at the folder that holds them (defaults to the
/// Logyx desktop folder). Tests are skipped when a file is missing.
/// </summary>
public static class Samples
{
    public static string Root =>
        Environment.GetEnvironmentVariable("DATAHUB_ORIS_SAMPLES")
        ?? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.Desktop), "logyx");

    public static string PathOf(string relative) => Path.Combine(Root, relative);
}

public sealed class SampleFactAttribute : FactAttribute
{
    public SampleFactAttribute(string relativePath)
    {
        if (!File.Exists(Samples.PathOf(relativePath)))
            Skip = $"Sample file not found: {Samples.PathOf(relativePath)} (set DATAHUB_ORIS_SAMPLES)";
    }
}

public class SampleFileTests
{
    private const string ReferenceWiring = "WIRING.TPS";
    private const string HiroFolder = @"ORIS DB\ORIS 5\HIRO";

    // Matches the ORIS export "TBC APIs Data Hub\WIRING-UNNAMED.CSV" exactly.
    [SampleFact(ReferenceWiring)]
    public void Journal_matches_reference_csv_row_count_and_total()
    {
        using var stream = File.OpenRead(Samples.PathOf(ReferenceWiring));
        var lines = OrisReader.ReadJournalLines(stream).ToList();

        Assert.Equal(16_781, lines.Count);
        Assert.Equal(10_525_411.79m, lines.Sum(l => l.Amount));
    }

    [SampleFact(ReferenceWiring)]
    public void Journal_lines_have_parsed_accounts_dates_and_georgian_text()
    {
        using var stream = File.OpenRead(Samples.PathOf(ReferenceWiring));
        var lines = OrisReader.ReadJournalLines(stream).ToList();

        Assert.All(lines, l => Assert.NotNull(l.OperationDate));
        Assert.True(lines.Count(l => l.Debit is not null && l.Credit is not null) > lines.Count * 0.99,
            "Nearly all lines should have parseable debit and credit accounts.");
        Assert.Contains(lines, l => l.Description.Any(c => c is >= 'ა' and <= 'ჰ'));
        Assert.DoesNotContain(lines, l => l.Description.Any(c => c is >= 'À' and <= 'ÿ'));
    }

    [SampleFact(HiroFolder + @"\Acc_name.tps")]
    public void Reads_chart_of_accounts_with_georgian_names()
    {
        using var stream = File.OpenRead(Samples.PathOf(HiroFolder + @"\Acc_name.tps"));
        var accounts = OrisReader.ReadAccountNames(stream).ToList();

        Assert.Equal(815, accounts.Count);
        Assert.Contains(accounts, a => a.Account?.Code == "3380" && a.Name == "ქონების გადასახადი");
    }

    [SampleFact(HiroFolder + @"\ACCOUNT.TPS")]
    public void Encrypted_file_raises_oris_file_exception()
    {
        using var stream = File.OpenRead(Samples.PathOf(HiroFolder + @"\ACCOUNT.TPS"));
        var ex = Assert.Throws<OrisFileException>(() => OrisReader.ReadJournalLines(stream).ToList());
        Assert.Equal("WIRING.TPS", ex.FileName);
    }
}
