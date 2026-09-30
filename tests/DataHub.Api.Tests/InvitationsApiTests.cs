using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using DataHub.Application;
using DataHub.Infrastructure.Tests;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace DataHub.Api.Tests;

public sealed class ApiFactory(SqlDatabaseFixture db) : WebApplicationFactory<Program>
{
    public FakeSms Sms { get; } = new();
    public FakeEmail Email { get; } = new();

    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        builder.UseEnvironment("Local");
        builder.UseSetting("ConnectionStrings:DataHub", db.Options.ConnectionString);
        builder.ConfigureServices(services =>
        {
            services.RemoveAll<ISmsSender>().AddSingleton<ISmsSender>(Sms);
            services.RemoveAll<IEmailSender>().AddSingleton<IEmailSender>(Email);
            services.RemoveAll<IFileStore>().AddSingleton<IFileStore, InMemoryFileStore>();
            services.RemoveAll<IJobPublisher>().AddSingleton<IJobPublisher, CapturingPublisher>();
        });
    }
}

[Collection(SqlCollection.Name)]
public sealed class InvitationsApiTests(SqlDatabaseFixture db) : IDisposable
{
    private readonly ApiFactory _factory = new(db);

    public void Dispose() => _factory.Dispose();

    private async Task<HttpClient> ClientAsync(string clientId = "tbc-los", string scope = "datahub.invitations")
    {
        var http = _factory.CreateClient();
        var token = await (await http.PostAsJsonAsync("/dev/token", new { clientId, scope })).Content.ReadFromJsonAsync<JsonElement>();
        http.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", token.GetProperty("access_token").GetString());
        return http;
    }

    private static object Body(string code, params string[] channels) => new
    {
        companyCode = code, companyName = "შპს ჰირო", email = "owner@hiro.ge", phone = "555 12 34 56", channels,
    };

    private static string NewCode() => Random.Shared.NextInt64(100_000_000, 999_999_999).ToString();

    [Fact]
    public async Task Requires_a_token()
    {
        var response = await _factory.CreateClient().PostAsJsonAsync("/invitations", Body(NewCode(), "sms"));
        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }

    [Fact]
    public async Task Requires_the_invitations_scope()
    {
        var http = await ClientAsync(scope: "datahub.reports");
        var response = await http.PostAsJsonAsync("/invitations", Body(NewCode(), "sms"));
        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
    }

    [Fact]
    public async Task Creates_invitation_sends_link_and_reports_status()
    {
        var http = await ClientAsync();
        var created = await http.PostAsJsonAsync("/invitations", Body(NewCode(), "sms", "email"));

        Assert.Equal(HttpStatusCode.Created, created.StatusCode);
        var body = await created.Content.ReadFromJsonAsync<JsonElement>();
        var link = body.GetProperty("link").GetString()!;
        Assert.StartsWith("http://datahub.localtest.me/i/", link);
        Assert.Equal(["sms", "email"], body.GetProperty("deliveredChannels").EnumerateArray().Select(e => e.GetString()));
        Assert.Contains(_factory.Sms.Sent, m => m.Phone == "+995555123456" && m.Text.Contains(link));
        Assert.Contains(_factory.Email.Sent, m => m.Body.Contains(link));

        var status = await http.GetFromJsonAsync<JsonElement>(created.Headers.Location);
        Assert.Equal("sent", status.GetProperty("status").GetString());
        Assert.Equal(body.GetProperty("tenantId").GetString(), status.GetProperty("tenantId").GetString());
    }

    [Fact]
    public async Task Idempotent_retry_returns_200_with_same_invitation()
    {
        var http = await ClientAsync();
        var code = NewCode();
        HttpRequestMessage Request() => new(HttpMethod.Post, "/invitations")
        {
            Content = JsonContent.Create(Body(code, "email")),
            Headers = { { "Idempotency-Key", "los-app-42" } },
        };

        var first = await http.SendAsync(Request());
        var retry = await http.SendAsync(Request());

        Assert.Equal(HttpStatusCode.Created, first.StatusCode);
        Assert.Equal(HttpStatusCode.OK, retry.StatusCode);
        Assert.Equal((await first.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("invitationId").GetString(),
                     (await retry.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("invitationId").GetString());
        Assert.Single(_factory.Email.Sent);
    }

    [Fact]
    public async Task Invalid_request_returns_problem_details_with_code()
    {
        var http = await ClientAsync();
        var response = await http.PostAsJsonAsync("/invitations", new { companyCode = "12", companyName = "x", channels = new[] { "fax" } });

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        var problem = await response.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal("INVITATION_INVALID", problem.GetProperty("code").GetString());
    }

    [Fact]
    public async Task Other_clients_cannot_see_an_invitation()
    {
        var los = await ClientAsync("tbc-los");
        var created = await los.PostAsJsonAsync("/invitations", Body(NewCode(), "email"));

        var other = await ClientAsync("tbc-crm");
        var response = await other.GetAsync(created.Headers.Location);
        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
    }
}
