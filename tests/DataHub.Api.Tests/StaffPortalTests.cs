using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using System.Text.RegularExpressions;
using DataHub.Api.Controllers;
using DataHub.Api.Staff;
using DataHub.Infrastructure.Persistence;
using DataHub.Infrastructure.Tests;
using DataHub.Oris;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.Abstractions;
using Microsoft.AspNetCore.Mvc.Filters;
using Microsoft.AspNetCore.Routing;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.FileProviders;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging.Abstractions;

namespace DataHub.Api.Tests;

/// <summary>The staff portal (/staff): a local test tool that sends invitations and opens reports without a token.</summary>
[Collection(SqlCollection.Name)]
public sealed partial class StaffPortalTests(SqlDatabaseFixture db) : IDisposable
{
    private readonly ApiFactory _factory = new(db);

    public void Dispose() => _factory.Dispose();

    private static string NewCode() => Random.Shared.NextInt64(100_000_000, 999_999_999).ToString();

    /// <summary>Loads the form (which sets the antiforgery cookie) and returns the client with the form's hidden fields.</summary>
    private async Task<(HttpClient Http, string Token, string RequestKey)> FormAsync()
    {
        var http = _factory.CreateClient();
        var page = await http.GetStringAsync("/staff");
        return (http, FormTokenRegex().Match(page).Groups[1].Value, RequestKeyRegex().Match(page).Groups[1].Value);
    }

    private static FormUrlEncodedContent Fields(string token, string requestKey, string code, string? email = "owner@hiro.ge",
        string? phone = "555 12 34 56", bool sendEmail = true, bool sendSms = true) => new(new Dictionary<string, string>
    {
        ["__RequestVerificationToken"] = token, ["RequestKey"] = requestKey,
        ["CompanyCode"] = code, ["CompanyName"] = "HIRO Test LLC", ["Email"] = email ?? "", ["Phone"] = phone ?? "",
        ["SendEmail"] = sendEmail ? "true" : "false", ["SendSms"] = sendSms ? "true" : "false",
    });

    [Fact]
    public async Task Shows_the_invitation_form()
    {
        var response = await _factory.CreateClient().GetAsync("/staff");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var html = await response.Content.ReadAsStringAsync();
        Assert.Contains("Send invitation", html);
        Assert.Matches(FormTokenRegex(), html);
        Assert.Contains("http://localhost:8025", html); // Mailpit link from appsettings.Local.json
    }

    [Fact]
    public async Task Sends_an_invitation_and_shows_its_link()
    {
        var (http, token, key) = await FormAsync();
        var code = NewCode();

        var response = await http.PostAsync("/staff/invitations", Fields(token, key, code));

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var html = WebUtility.HtmlDecode(await response.Content.ReadAsStringAsync());
        Assert.Contains("Invitation sent by email + SMS", html);
        var link = LinkRegex().Match(html).Value;
        Assert.StartsWith("https://localhost:7049/i/", link);
        Assert.Contains(_factory.Email.Sent, m => m.Body.Contains(link));
        Assert.Contains(_factory.Sms.Sent, m => m.Phone == "+995555123456" && m.Text.Contains(link));

        await using var context = db.CreateContext();
        var invitation = await context.Invitations.Include(i => i.Company).SingleAsync(i => i.Company.CompanyCode == code);
        Assert.Equal(StaffController.ClientId, invitation.CreatedByClient);

        // The list on the form page shows it.
        var list = await http.GetStringAsync("/staff");
        Assert.Contains($"/staff/invitations/{invitation.Id}", list);
        Assert.Contains("Sent, waiting for the customer", list);
    }

    [Fact]
    public async Task Sending_the_same_form_twice_sends_one_invitation()
    {
        var (http, token, key) = await FormAsync();
        var code = NewCode();

        await http.PostAsync("/staff/invitations", Fields(token, key, code, phone: null, sendSms: false));
        var again = await http.PostAsync("/staff/invitations", Fields(token, key, code, phone: null, sendSms: false));

        var html = await again.Content.ReadAsStringAsync();
        Assert.Contains("already submitted", html);
        await using var context = db.CreateContext();
        Assert.Equal(1, await context.Invitations.CountAsync(i => i.Company.CompanyCode == code));
        var link = LinkRegex().Match(html).Value;
        Assert.Single(_factory.Email.Sent, m => m.Body.Contains(link));
    }

    [Fact]
    public async Task Invalid_input_is_shown_on_the_form_and_nothing_is_sent()
    {
        var (http, token, key) = await FormAsync();

        var response = await http.PostAsync("/staff/invitations", Fields(token, key, "12345", email: null, sendSms: false));

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var html = await response.Content.ReadAsStringAsync();
        Assert.Contains("companyCode must be 9 or 11 digits.", html);
        Assert.Contains("email is required for the &#x27;email&#x27; channel.", html);
        Assert.Contains("value=\"12345\"", html); // the entered values are kept
        Assert.Empty(_factory.Email.Sent);
    }

    [Fact]
    public async Task Posting_without_the_form_token_is_refused()
    {
        var response = await _factory.CreateClient().PostAsync("/staff/invitations", Fields("", Guid.NewGuid().ToString("N"), NewCode()));

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Empty(_factory.Email.Sent);
    }

    [Fact]
    public async Task Details_show_the_data_period_and_the_reports_open_as_json()
    {
        var (http, token, key) = await FormAsync();
        var code = NewCode();
        await http.PostAsync("/staff/invitations", Fields(token, key, code));

        await using var context = db.CreateContext();
        var invitation = await context.Invitations.Include(i => i.Company).SingleAsync(i => i.Company.CompanyCode == code);
        var details = $"/staff/invitations/{invitation.Id}";
        Assert.Contains("Available once the company", await http.GetStringAsync(details));

        await ActivateDataAsync(context, invitation.CompanyId);

        var html = await http.GetStringAsync(details);
        Assert.Contains("<strong>2024-01-15</strong> to <strong>2024-02-01</strong>", html);

        var tenant = invitation.Company.TenantId;
        var journal = await http.GetFromJsonAsync<JsonElement>($"/staff/reports/{tenant}/journal-entries?fromDate=2024-01-01&toDate=2024-12-31");
        Assert.Equal(2, journal.GetProperty("totalCount").GetInt32());
        var turnover = await http.GetAsync($"/staff/reports/{tenant}/turnover-register?fromDate=2024-01-01&toDate=2024-12-31");
        Assert.Equal(HttpStatusCode.OK, turnover.StatusCode);
        var balance = await http.GetAsync($"/staff/reports/{tenant}/balance-sheet?toDate=2024-12-31");
        Assert.Equal(HttpStatusCode.OK, balance.StatusCode);

        var noDates = await http.GetAsync($"/staff/reports/{tenant}/journal-entries");
        Assert.Equal(HttpStatusCode.BadRequest, noDates.StatusCode);
        var unknownTenant = await http.GetAsync($"/staff/reports/{Guid.NewGuid()}/balance-sheet?toDate=2024-12-31");
        Assert.Equal(HttpStatusCode.NotFound, unknownTenant.StatusCode);
    }

    [Fact]
    public async Task Unknown_invitation_is_404()
    {
        var response = await _factory.CreateClient().GetAsync($"/staff/invitations/{Guid.NewGuid()}");
        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
    }

    [Theory]
    [InlineData("Production", true)]
    [InlineData("Development", true)]
    [InlineData("Local", false)]
    public void Answers_404_outside_the_Local_environment(string environment, bool notFound)
    {
        var http = new DefaultHttpContext
        {
            RequestServices = new ServiceCollection().AddSingleton<IHostEnvironment>(new TestEnvironment(environment)).BuildServiceProvider(),
        };
        var context = new AuthorizationFilterContext(new ActionContext(http, new RouteData(), new ActionDescriptor()), []);

        new LocalOnlyAttribute().OnAuthorization(context);

        Assert.Equal(notFound, context.Result is NotFoundResult);
    }

    private static async Task ActivateDataAsync(DataHubDbContext context, Guid companyId)
    {
        OrisJournalLine Line(DateOnly date, string debit, string credit, decimal amount) =>
            new(1, "DOC-1", 1, OrisAccount.Parse(debit), debit, OrisAccount.Parse(credit), credit, amount, "GEL",
                "operation", null, "", "operator", date, date);

        var store = new SqlDatasetStore(context, NullLogger<SqlDatasetStore>.Instance, TimeProvider.System);
        var dataset = await store.CreateStagingAsync(companyId, Guid.NewGuid(), default);
        await store.WriteJournalAsync(dataset, new GelConverter([]).Apply(
        [
            Line(new(2024, 1, 15), "1 2 10", "6 1 10", 100m),
            Line(new(2024, 2, 1), "7 4 10", "1 2 10", 16.87m),
        ]), default);
        await store.ActivateAsync(dataset, default);
    }

    private sealed class TestEnvironment(string name) : IHostEnvironment
    {
        public string EnvironmentName { get; set; } = name;
        public string ApplicationName { get; set; } = "test";
        public string ContentRootPath { get; set; } = "";
        public IFileProvider ContentRootFileProvider { get; set; } = new NullFileProvider();
    }

    [GeneratedRegex(@"name=""__RequestVerificationToken"" type=""hidden"" value=""([^""]+)""")]
    private static partial Regex FormTokenRegex();

    [GeneratedRegex(@"name=""RequestKey"" value=""([0-9a-f]{32})""")]
    private static partial Regex RequestKeyRegex();

    [GeneratedRegex(@"https://localhost:7049/i/[A-Za-z0-9_\-]+")]
    private static partial Regex LinkRegex();
}
