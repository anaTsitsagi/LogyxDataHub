namespace DataHub.Oris.Tests;

public class GeorgianTextTests
{
    [Theory]
    [InlineData("ÛÄÞÄÍÉËÉÀ ÓÀØÏÍÄËÉ", "შეძენილია საქონელი")]
    [InlineData("ÍÀÈÉÀ áÅÄÃÄËÉÞÄ", "ნათია ხვედელიძე")]
    [InlineData(" ÌÈ.ßÉÂÍÉ    ", "მთ.წიგნი")]
    [InlineData("ÝÀËÉ", "ცალი")]
    public void Decodes_oris_bytes_to_unicode_georgian(string latin1, string expected) =>
        Assert.Equal(expected, GeorgianText.Decode(latin1));

    [Fact]
    public void Leaves_latin_text_and_digits_unchanged() =>
        Assert.Equal("Tbilisi Energy 35001090041", GeorgianText.Decode("Tbilisi Energy 35001090041   "));

    [Fact]
    public void Null_or_empty_becomes_empty() =>
        Assert.Equal(string.Empty, GeorgianText.Decode(null));
}

public class ClarionDateTests
{
    [Theory]
    [InlineData(4, 1801, 1, 1)]
    [InlineData(79627, 2019, 1, 1)]
    [InlineData(79991, 2019, 12, 31)]
    [InlineData(80692, 2021, 12, 1)]
    public void Converts_days_since_1800_12_28(long days, int y, int m, int d) =>
        Assert.Equal(new DateOnly(y, m, d), ClarionDate.ToDateOnly(days));

    [Fact]
    public void Zero_means_no_date() => Assert.Null(ClarionDate.ToDateOnly(0));

    [Fact]
    public void Round_trips() =>
        Assert.Equal(79991, ClarionDate.FromDateOnly(new DateOnly(2019, 12, 31)));
}

public class OrisAccountTests
{
    [Theory]
    [InlineData("3 1 20 1 193", "3120", "1 193")]
    [InlineData("1 6 20 00255             ", "1620", "00255")]
    [InlineData("3 1 10 82", "3110", "82")]
    [InlineData("8 2 10", "8210", "")]
    [InlineData("3 4 10 3", "3410", "3")]
    public void First_four_digits_are_the_account_rest_are_nested_subaccounts(string raw, string code, string sub)
    {
        var account = OrisAccount.Parse(raw);
        Assert.Equal(code, account.Code);
        Assert.Equal(sub, account.Sub);
    }

    [Fact]
    public void Each_following_group_is_a_sub_level_of_the_previous() =>
        Assert.Equal(["1", "193"], OrisAccount.Parse("3 1 20 1 193").SubAccounts);

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("1 2")]          // too short
    [InlineData("1 2 345")]      // overshoots 4 digits
    [InlineData("A 1 20")]       // not digits
    public void Rejects_invalid_accounts(string raw) =>
        Assert.False(OrisAccount.TryParse(raw, out _));
}
