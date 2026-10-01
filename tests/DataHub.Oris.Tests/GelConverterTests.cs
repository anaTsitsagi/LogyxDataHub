namespace DataHub.Oris.Tests;

public class GelConverterTests
{
    private static OrisJournalLine Line(decimal amount, string currency, DateOnly? date, decimal? rate = null) =>
        new(7, "D", 1, null, "", null, "", amount, currency, "", null, "", "", date, date) { ExchangeRate = rate };

    private static readonly GelConverter Converter = new(
    [
        new OrisRate("USD", new DateOnly(2024, 1, 10), 2.7000m),
        new OrisRate("USD", new DateOnly(2024, 1, 12), 2.6800m),
        new OrisRate("EUR", new DateOnly(2024, 1, 10), 2.9500m),
    ]);

    private static OrisJournalLine Convert(OrisJournalLine line) => Converter.Apply([line]).Single();

    [Fact]
    public void Gel_and_blank_currency_lines_keep_their_amount()
    {
        Assert.Equal(100m, Convert(Line(100m, "GEL", new DateOnly(2024, 1, 1))).AmountGel);
        var revaluation = Convert(Line(4.31m, "", new DateOnly(2024, 6, 30)));
        Assert.Equal((4.31m, "GEL"), (revaluation.AmountGel!.Value, revaluation.Currency));
    }

    [Fact]
    public void Rate_recorded_on_the_line_wins_over_the_table() =>
        Assert.Equal(144.63m, Convert(Line(50m, "USD", new DateOnly(2024, 1, 10), rate: 2.8926m)).AmountGel);

    [Theory]
    [InlineData(10, 135.00)] // exact date
    [InlineData(11, 135.00)] // no rate that day: latest earlier rate
    [InlineData(12, 134.00)]
    public void Table_rate_is_the_latest_on_or_before_the_operation_date(int day, double expected) =>
        Assert.Equal((decimal)expected, Convert(Line(50m, "USD", new DateOnly(2024, 1, day))).AmountGel);

    [Fact]
    public void Rounds_half_away_from_zero_like_oris() =>
        Assert.Equal(0.03m, Convert(Line(0.01m, "EUR", new DateOnly(2024, 1, 10), rate: 2.5m)).AmountGel); // 0.025 → 0.03

    [Fact]
    public void Missing_rate_is_reported_with_the_record()
    {
        var ex = Assert.Throws<MissingRateException>(() => Convert(Line(10m, "USD", new DateOnly(2024, 1, 9))));
        Assert.Contains("record 7", ex.Message);
        Assert.Throws<MissingRateException>(() => Convert(Line(10m, "RUB", new DateOnly(2024, 1, 20))));
    }
}
