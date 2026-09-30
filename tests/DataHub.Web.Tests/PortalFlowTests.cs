using System.IO.Compression;
using System.Net;
using System.Net.Http.Json;
using System.Security.Cryptography;
using System.Text.Json;
using System.Text.RegularExpressions;
using DataHub.Application;
using DataHub.Application.Invitations;
using DataHub.Domain;
using DataHub.Infrastructure.Tests;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace DataHub.Web.Tests;

[CollectionDefinition(SqlCollection.Name)]
public sealed class WebSqlCollection : ICollectionFixture<SqlDatabaseFixture>;

public sealed class PortalFactory(SqlDatabaseFixture db) : WebApplicationFactory<Program>
{
    public const int Chunk = 1024;
    public FakeSms Sms { get; } = new();
    public FakeEmail Email { get; } = new();
    public CapturingPublisher Publisher { get; } = new();
    public InMemoryFileStore Files { get; } = new();

    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        builder.UseEnvironment("Local");
        builder.UseSetting("ConnectionStrings:DataHub", db.Options.ConnectionString);
        builder.UseSetting("Uploads:ChunkSizeBytes", Chunk.ToString());
        builder.ConfigureServices(services =>
        {
            services.RemoveAll<ISmsSender>().AddSingleton<ISmsSender>(Sms);
            services.RemoveAll<IEmailSender>().AddSingleton<IEmailSender>(Email);
            services.RemoveAll<IFileStore>().AddSingleton<IFileStore>(Files);
            services.RemoveAll<IJobPublisher>().AddSingleton<IJobPublisher>(Publisher);
        });
    }
}

[Collection(SqlCollection.Name)]
public sealed partial class PortalFlowTests(SqlDatabaseFixture db) : IDisposable
{
    private readonly PortalFactory _factory = new(db);

    public void Dispose() => _factory.Dispose();

    // __Host- cookies are Secure-only, so talk to the test server over https.
    private HttpClient Browser() => _factory.CreateClient(new WebApplicationFactoryClientOptions
    {
        BaseAddress = new Uri("https://localhost"),
        AllowAutoRedirect = false,
    });

    private async Task<(string Token, string Code)> InviteAsync(NotificationChannels channels = NotificationChannels.Email)
    {
        using var scope = _factory.Services.CreateScope();
        var code = Random.Shared.NextInt64(100_000_000, 999_999_999).ToString();
        var result = await scope.ServiceProvider.GetRequiredService<InvitationService>().CreateAsync(
            new CreateInvitationCommand(code, "შპს ჰირო", "owner@hiro.ge", "555123456", channels, null, "tbc-los"), default);
        return (result.Link[(result.Link.LastIndexOf('/') + 1)..], code);
    }

    private static string FormToken(string html) => FormTokenRegex().Match(html).Groups[1].Value;

    private static string MetaToken(string html) => MetaTokenRegex().Match(html).Groups[1].Value;

    /// <summary>Walks the link → details → code pages and returns a signed-in browser plus the page's CSRF token.</summary>
    private async Task<(HttpClient Browser, string Csrf)> SignInAsync()
    {
        var (token, companyCode) = await InviteAsync();
        var browser = Browser();

        var linkPage = await browser.GetStringAsync($"/i/{token}");
        Assert.Contains("შპს ჰირო", linkPage);

        var start = await browser.PostAsync($"/i/{token}", new FormUrlEncodedContent(new Dictionary<string, string>
        {
            ["CompanyCode"] = companyCode, ["Email"] = "owner@hiro.ge", ["__RequestVerificationToken"] = FormToken(linkPage),
        }));
        Assert.Equal(HttpStatusCode.Redirect, start.StatusCode);
        var codeUrl = start.Headers.Location!.ToString();

        var codePage = await browser.GetStringAsync(codeUrl);
        Assert.Contains("o***@hiro.ge", codePage);
        var otp = SixDigits().Match(_factory.Email.Sent[^1].Body).Value;

        var confirm = await browser.PostAsync(codeUrl.Split('?')[0], new FormUrlEncodedContent(new Dictionary<string, string>
        {
            ["code"] = otp, ["token"] = token, ["__RequestVerificationToken"] = FormToken(codePage),
        }));
        Assert.Equal("/upload", confirm.Headers.Location?.ToString());

        var uploadPage = await browser.GetStringAsync("/upload");
        Assert.Contains("orisDatabase", uploadPage);
        Assert.Contains("disabled", uploadPage); // CSV option is shown but not yet available
        return (browser, MetaToken(uploadPage));
    }

    private static byte[] Zip()
    {
        using var ms = new MemoryStream();
        using (var zip = new ZipArchive(ms, ZipArchiveMode.Create, leaveOpen: true))
        using (var s = zip.CreateEntry("HIRO/WIRING.TPS").Open())
            s.Write(RandomNumberGenerator.GetBytes(2500));
        return ms.ToArray();
    }

    private static HttpRequestMessage Json(HttpMethod method, string url, string csrf, object? body = null) => new(method, url)
    {
        Content = body is null ? null : JsonContent.Create(body),
        Headers = { { "X-CSRF-TOKEN", csrf } },
    };

    [Fact]
    public async Task Customer_verifies_uploads_in_chunks_and_job_is_queued()
    {
        var (browser, csrf) = await SignInAsync();
        var file = Zip();

        var start = await browser.SendAsync(Json(HttpMethod.Post, "/portal-api/uploads", csrf,
            new { type = "orisDatabase", fileName = "ჰირო.zip", sizeBytes = file.Length }));
        start.EnsureSuccessStatusCode();
        var session = await start.Content.ReadFromJsonAsync<JsonElement>();
        var uploadId = session.GetProperty("uploadId").GetString();
        int parts = session.GetProperty("partCount").GetInt32();
        Assert.True(parts > 1);

        for (int n = 1; n <= parts; n++)
        {
            var slice = file.Skip((n - 1) * PortalFactory.Chunk).Take(PortalFactory.Chunk).ToArray();
            var put = new HttpRequestMessage(HttpMethod.Put, $"/portal-api/uploads/{uploadId}/parts/{n}")
            {
                Content = new ByteArrayContent(slice) { Headers = { ContentType = new("application/octet-stream") } },
                Headers = { { "X-CSRF-TOKEN", csrf } },
            };
            Assert.Equal(HttpStatusCode.NoContent, (await browser.SendAsync(put)).StatusCode);
        }

        var complete = await browser.SendAsync(Json(HttpMethod.Post, $"/portal-api/uploads/{uploadId}/complete", csrf, new { sha256 = (string?)null }));
        complete.EnsureSuccessStatusCode();

        var message = Assert.Single(_factory.Publisher.Published);
        Assert.Equal(file, _factory.Files.Objects[message.S3Key]);
        Assert.EndsWith("/zip", message.S3Key); // Georgian file name → ASCII-safe object key

        var status = await browser.GetFromJsonAsync<JsonElement>("/portal-api/status");
        Assert.Equal("queued", status.GetProperty("jobStatus").GetString());
        Assert.Equal("/status", (await browser.GetAsync("/upload")).Headers.Location?.ToString());
    }

    [Fact]
    public async Task Invalid_zip_returns_bilingual_error_code()
    {
        var (browser, csrf) = await SignInAsync();
        var response = await browser.SendAsync(Json(HttpMethod.Post, "/portal-api/uploads", csrf,
            new { type = "orisEntriesCsv", fileName = "entries.csv", sizeBytes = 100 }));

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        var error = await response.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal("UPLOAD_TYPE_DISABLED", error.GetProperty("code").GetString());
        Assert.False(string.IsNullOrEmpty(error.GetProperty("message").GetProperty("ka").GetString()));
    }

    [Fact]
    public async Task Upload_api_requires_a_verified_session()
    {
        var response = await Browser().PostAsJsonAsync("/portal-api/uploads", new { type = "orisDatabase", fileName = "a.zip", sizeBytes = 10 });
        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }

    [Fact]
    public async Task Upload_api_rejects_requests_without_csrf_token()
    {
        var (browser, _) = await SignInAsync();
        var response = await browser.PostAsJsonAsync("/portal-api/uploads", new { type = "orisDatabase", fileName = "a.zip", sizeBytes = 10 });
        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }

    [Fact]
    public async Task Wrong_company_code_shows_error_and_sends_no_code()
    {
        var (token, _) = await InviteAsync();
        var browser = Browser();
        var page = await browser.GetStringAsync($"/i/{token}");
        int emailsBefore = _factory.Email.Sent.Count;

        var response = await browser.PostAsync($"/i/{token}", new FormUrlEncodedContent(new Dictionary<string, string>
        {
            ["CompanyCode"] = "111111111", ["Email"] = "owner@hiro.ge", ["__RequestVerificationToken"] = FormToken(page),
        }));

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Contains("do not match", await response.Content.ReadAsStringAsync());
        Assert.Equal(emailsBefore, _factory.Email.Sent.Count);
    }

    [Fact]
    public async Task Unknown_link_shows_invalid_page_with_security_headers()
    {
        var response = await Browser().GetAsync("/i/not-a-real-token");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Contains("expired", await response.Content.ReadAsStringAsync());
        Assert.Equal("no-referrer", response.Headers.GetValues("Referrer-Policy").Single());
        Assert.Contains("frame-ancestors 'none'", response.Headers.GetValues("Content-Security-Policy").Single());
    }

    [GeneratedRegex(@"name=""__RequestVerificationToken"" type=""hidden"" value=""([^""]+)""")]
    private static partial Regex FormTokenRegex();

    [GeneratedRegex(@"<meta name=""csrf-token"" content=""([^""]+)""")]
    private static partial Regex MetaTokenRegex();

    [GeneratedRegex(@"\b\d{6}\b")]
    private static partial Regex SixDigits();
}
