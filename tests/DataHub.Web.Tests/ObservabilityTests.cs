using System.Collections.Concurrent;
using System.Net;
using DataHub.Application.Invitations;
using DataHub.Domain;
using DataHub.Hosting;
using DataHub.Infrastructure.Tests;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace DataHub.Web.Tests;

public sealed class ObservabilityUnitTests
{
    [Theory]
    [InlineData("/i/AbC-123_tok", "/i/***")]
    [InlineData("/I/AbC-123_tok/extra", "/i/***")]
    [InlineData("/i", "/i")]
    [InlineData("/i/", "/i/")]
    [InlineData("/index", "/index")]
    [InlineData("/verify/0b9d6b5e-1d1c-4a7e-9a7a-5b8b2a0c9e11", "/verify/0b9d6b5e-1d1c-4a7e-9a7a-5b8b2a0c9e11")]
    [InlineData("/portal-api/uploads", "/portal-api/uploads")]
    public void Link_token_is_redacted_from_paths(string path, string expected) =>
        Assert.Equal(expected, SensitiveData.RedactPath(new PathString(path)));

    [Fact]
    public void Otlp_headers_and_signal_addresses_are_parsed()
    {
        var o = new OtlpOptions { Endpoint = "http://seq:5341/ingest/otlp/", Headers = " X-Seq-ApiKey = abc=def , bad, =x " };

        Assert.Equal(new Dictionary<string, string> { ["X-Seq-ApiKey"] = "abc=def" }, o.ParseHeaders());
        Assert.Equal("http://seq:5341/ingest/otlp/v1/logs", o.SignalUri("logs").ToString());
        Assert.Equal("http://seq:5341/ingest/otlp/v1/traces", o.SignalUri("traces").ToString());
    }
}

[Collection(SqlCollection.Name)]
public sealed class ReplicaTests(SqlDatabaseFixture db)
{
    [Fact]
    public void Cookies_protected_by_one_replica_are_readable_by_another()
    {
        using var first = new PortalFactory(db);
        using var second = new PortalFactory(db);
        const string purpose = "Microsoft.AspNetCore.Authentication.Cookies";

        var protectedValue = first.Services.GetRequiredService<IDataProtectionProvider>().CreateProtector(purpose).Protect("session");

        Assert.Equal("session", second.Services.GetRequiredService<IDataProtectionProvider>().CreateProtector(purpose).Unprotect(protectedValue));
        // Shared through the database, not a machine-local key folder (which pods do not share).
        using var context = db.CreateContext();
        Assert.NotEmpty(context.DataProtectionKeys.ToList());
    }
}

[Collection(SqlCollection.Name)]
public sealed class RequestLoggingTests(SqlDatabaseFixture db)
{
    private readonly LogCapture _logs = new();

    [Fact]
    public async Task Request_log_never_contains_the_link_token()
    {
        using var factory = new PortalFactory(db).WithWebHostBuilder(b =>
            b.ConfigureLogging(l => l.AddProvider(_logs)));
        string token;
        using (var scope = factory.Services.CreateScope())
        {
            var code = Random.Shared.NextInt64(100_000_000, 999_999_999).ToString();
            var link = (await scope.ServiceProvider.GetRequiredService<InvitationService>().CreateAsync(
                new CreateInvitationCommand(code, "Log Test", "owner@example.ge", "555123456", NotificationChannels.Email, null, "tbc-los"), default)).Link;
            token = link[(link.LastIndexOf('/') + 1)..];
        }

        var browser = factory.CreateClient(new WebApplicationFactoryClientOptions { BaseAddress = new Uri("https://localhost") });
        Assert.Equal(HttpStatusCode.OK, (await browser.GetAsync($"/i/{token}")).StatusCode);
        await browser.GetAsync($"/i/{token}x"); // the invalid-link page must not log the attempted token either

        // The request line is written when the response completes.
        for (int i = 0; i < 50 && !_logs.Messages.Any(m => m.Contains("/i/***")); i++) await Task.Delay(20);

        Assert.Contains(_logs.Messages, m => m.StartsWith("HTTP \"GET\" \"/i/***\" responded 200"));
        Assert.DoesNotContain(_logs.Messages, m => m.Contains(token));
        Assert.DoesNotContain(_logs.Messages, m => m.Contains("owner@example.ge") || m.Contains("555123456"));
    }

    private sealed class LogCapture : ILoggerProvider
    {
        public ConcurrentQueue<string> Messages { get; } = new();

        public ILogger CreateLogger(string categoryName) => new Logger(this);

        public void Dispose() { }

        private sealed class Logger(LogCapture owner) : ILogger
        {
            public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;

            public bool IsEnabled(LogLevel logLevel) => true;

            public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter) =>
                owner.Messages.Enqueue(formatter(state, exception) + exception);
        }
    }
}
