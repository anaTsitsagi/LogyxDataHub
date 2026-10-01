using DataHub.Application;
using DataHub.Application.Reports;

namespace DataHub.Infrastructure.Tests;

public class AccountFilterTests
{
    [Theory]
    [InlineData("1 2 10", "1210", "", "1210")]
    [InlineData("1210", "1210", "", "1210")]
    [InlineData("1400", "14", "", "1400")]      // group
    [InlineData("1 0 00", "1", "", "1000")]    // class
    [InlineData("1410 1 267", "1410", "1 267", "1410 1 267")]
    [InlineData("3 1 20 1", "3120", "1", "3120 1")]
    public void Parses_oris_and_compact_forms(string input, string prefix, string sub, string key)
    {
        var f = AccountFilter.Parse(input);
        Assert.Equal((prefix, sub, key), (f.CodePrefix, f.Sub, f.Key));
    }

    [Theory]
    [InlineData("1410 1", "1410", "1", true)]
    [InlineData("1410 1", "1410", "1 267", true)]
    [InlineData("1410 1", "1410", "12", false)]  // "12" is not under "1"
    [InlineData("1410 1", "1410", "", false)]
    [InlineData("1400", "1480", "", true)]
    [InlineData("1400", "1500", "", false)]
    public void Matches_the_account_and_everything_nested_under_it(string filter, string code, string sub, bool expected) =>
        Assert.Equal(expected, AccountFilter.Parse(filter).Matches(code, sub));

    [Theory]
    [InlineData("12x")]
    [InlineData("12")]
    [InlineData("")]
    public void Rejects_invalid_account_numbers(string input) =>
        Assert.Equal("ACCOUNT_INVALID", Assert.Throws<DataHubException>(() => AccountFilter.Parse(input)).Code);
}
