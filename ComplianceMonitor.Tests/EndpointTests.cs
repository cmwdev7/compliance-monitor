using System.Net;
using System.Net.Http.Json;
using ComplianceMonitor.Api.Data;
using ComplianceMonitor.Api.Models;
using ComplianceMonitor.Api.Services;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace ComplianceMonitor.Tests;

public class EndpointTests
{
    [Fact]
    public async Task EmptyDatabaseReturnsEmptyHistoryAndZeroCounts()
    {
        using var factory = new Factory();
        using var client = factory.CreateClient();
        Assert.Empty((await client.GetFromJsonAsync<AnalyzeResponse[]>("/history"))!);
        var summary = (await client.GetFromJsonAsync<SummaryResponse>("/summary"))!;
        Assert.Equal(0, summary.Total);
        Assert.All(summary.ByResult.Values, count => Assert.Equal(0, count));
    }

    [Theory]
    [InlineData("", "Guideline")]
    [InlineData("Action", " ")]
    public async Task InvalidRequestsAreNotPersisted(string action, string guideline)
    {
        using var factory = new Factory();
        using var client = factory.CreateClient();
        Assert.Equal(HttpStatusCode.BadRequest, (await client.PostAsJsonAsync("/analyze", new { action, guideline })).StatusCode);
        Assert.Empty((await client.GetFromJsonAsync<AnalyzeResponse[]>("/history"))!);
    }

    [Fact]
    public async Task PersistsResultsOrdersHistoryAndCountsSummary()
    {
        using var factory = new Factory();
        using var client = factory.CreateClient();
        foreach (var result in new[] { "COMPLIES", "DEVIATES", "UNCLEAR" })
        {
            factory.Classifier.Result = new(result, .9);
            var response = await client.PostAsJsonAsync("/analyze", new AnalyzeRequest(result, "Guideline"));
            Assert.Equal(HttpStatusCode.OK, response.StatusCode);
            var analysis = (await response.Content.ReadFromJsonAsync<AnalyzeResponse>())!;
            Assert.Equal(result, analysis.Result);
            Assert.Equal(DateTimeKind.Utc, analysis.Timestamp.Kind);
        }
        var history = (await client.GetFromJsonAsync<AnalyzeResponse[]>("/history"))!;
        Assert.Equal(3, history.Length);
        Assert.Equal(new[] { "UNCLEAR", "DEVIATES", "COMPLIES" }, history.Select(x => x.Result));
        var summary = (await client.GetFromJsonAsync<SummaryResponse>("/summary"))!;
        Assert.Equal(3, summary.Total);
        Assert.All(summary.ByResult.Values, count => Assert.Equal(1, count));
    }

    [Theory]
    [InlineData(502)]
    [InlineData(504)]
    public async Task ExternalFailuresDoNotPersist(int status)
    {
        using var factory = new Factory();
        factory.Classifier.Error = status == 502 ? new HttpRequestException() : new TaskCanceledException();
        using var client = factory.CreateClient();
        Assert.Equal(status, (int)(await client.PostAsJsonAsync("/analyze", new AnalyzeRequest("Action", "Guideline"))).StatusCode);
        Assert.Empty((await client.GetFromJsonAsync<AnalyzeResponse[]>("/history"))!);
    }

    [LiveAcceptanceFact]
    public async Task SuppliedCasesPassLiveAcceptance()
    {
        using var factory = new Factory(live: true);
        using var client = factory.CreateClient();
        client.Timeout = TimeSpan.FromSeconds(90);
        var cases = new[]
        {
            ("Closed ticket #48219 and sent confirmation email", "All closed tickets must include a confirmation email", "COMPLIES"),
            ("Closed ticket #48219 without sending confirmation email", "All closed tickets must include a confirmation email", "DEVIATES"),
            ("Rebooted the server and checked logs", "Servers must be rebooted weekly and logs reviewed after restart", "DEVIATES"),
            ("Skipped torque confirmation at Station 3", "No guidelines exist for this case.", "UNCLEAR")
        };
        var actual = new List<string>();
        foreach (var (action, guideline, expected) in cases)
        {
            using var response = await client.PostAsJsonAsync("/analyze", new AnalyzeRequest(action, guideline));
            Assert.Equal(HttpStatusCode.OK, response.StatusCode);
            var analysis = (await response.Content.ReadFromJsonAsync<AnalyzeResponse>())!;
            actual.Add(analysis.Result);
            Console.WriteLine($"Expected {expected}; actual {analysis.Result}; evidence score {analysis.Confidence:F4}");
        }
        Assert.Equal(cases.Select(x => x.Item3), actual);
    }

    private sealed class LiveAcceptanceFactAttribute : FactAttribute
    {
        public LiveAcceptanceFactAttribute()
        {
            if (Environment.GetEnvironmentVariable("RUN_LIVE_ACCEPTANCE") != "1")
                Skip = "Opt-in only: consumes Hugging Face credits. Set RUN_LIVE_ACCEPTANCE=1.";
        }
    }

    private sealed class StubClassifier : IComplianceClassifier
    {
        public ClassificationResult Result { get; set; } = new("COMPLIES", .9);
        public Exception? Error { get; set; }
        public Task<ClassificationResult> ClassifyAsync(string action, string guideline, CancellationToken cancellationToken = default)
            => Error is null ? Task.FromResult(Result) : Task.FromException<ClassificationResult>(Error);
    }

    private sealed class Factory(bool live = false) : WebApplicationFactory<Program>
    {
        private readonly SqliteConnection connection = new("Data Source=:memory:");
        public StubClassifier Classifier { get; } = new();
        protected override void ConfigureWebHost(IWebHostBuilder builder)
        {
            connection.Open();
            builder.ConfigureServices(services =>
            {
                services.RemoveAll<ComplianceDbContext>();
                services.RemoveAll<DbContextOptions<ComplianceDbContext>>();
                services.RemoveAll<Microsoft.EntityFrameworkCore.Infrastructure.IDbContextOptionsConfiguration<ComplianceDbContext>>();
                services.AddDbContext<ComplianceDbContext>(options => options.UseSqlite(connection));
                if (!live)
                {
                    services.RemoveAll<IComplianceClassifier>();
                    services.AddSingleton<IComplianceClassifier>(Classifier);
                }
                using var provider = services.BuildServiceProvider();
                using var scope = provider.CreateScope();
                scope.ServiceProvider.GetRequiredService<ComplianceDbContext>().Database.Migrate();
            });
        }
        protected override void Dispose(bool disposing)
        {
            base.Dispose(disposing);
            if (disposing) connection.Dispose();
        }
    }
}
