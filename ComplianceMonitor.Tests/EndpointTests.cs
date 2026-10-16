using System.Net;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using ComplianceMonitor.Api.Data;
using ComplianceMonitor.Api.Models;
using ComplianceMonitor.Api.Services;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Configuration;

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
        Assert.Equal(0, factory.Classifier.Calls);
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

    [Fact]
    public async Task EqualTimestampsUseNewestIdFirst()
    {
        using var factory = new Factory();
        using var client = factory.CreateClient();
        using var scope = factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<ComplianceDbContext>();
        var timestamp = DateTime.UtcNow;
        db.Analyses.AddRange(
            new AnalysisRecord { Action = "First", Guideline = "Rule", Result = "COMPLIES", Confidence = .9, Timestamp = timestamp },
            new AnalysisRecord { Action = "Second", Guideline = "Rule", Result = "COMPLIES", Confidence = .9, Timestamp = timestamp });
        await db.SaveChangesAsync();
        var history = (await client.GetFromJsonAsync<AnalyzeResponse[]>("/history"))!;
        Assert.Equal(new[] { "Second", "First" }, history.Select(x => x.Action));
    }

    [Theory]
    [InlineData("{}", "application/json", 400)]
    [InlineData("null", "application/json", 400)]
    [InlineData("{\"action\":null,\"guideline\":\"Rule\"}", "application/json", 400)]
    [InlineData("{\"action\":\"Action\",\"guideline\":null}", "application/json", 400)]
    [InlineData("{\"action\":42,\"guideline\":\"Rule\"}", "application/json", 400)]
    [InlineData("{\"action\":\"Action\",\"guideline\":{}}", "application/json", 400)]
    [InlineData("{\"action\":", "application/json", 400)]
    [InlineData("", "application/json", 400)]
    [InlineData("Action", "text/plain", 415)]
    public async Task BadBodiesNeverInvokeClassifierOrPersist(string body, string contentType, int expectedStatus)
    {
        using var factory = new Factory();
        using var client = factory.CreateClient();
        using var response = await client.PostAsync("/analyze", new StringContent(body, Encoding.UTF8, contentType));
        Assert.Equal(expectedStatus, (int)response.StatusCode);
        Assert.Equal(0, factory.Classifier.Calls);
        Assert.Empty((await client.GetFromJsonAsync<AnalyzeResponse[]>("/history"))!);
    }

    [Theory]
    [InlineData("malformed", 502)]
    [InlineData("configuration", 502)]
    [InlineData("timeout", 504)]
    public async Task ClassifierErrorsAreSanitizedAndNotPersisted(string failure, int expectedStatus)
    {
        using var factory = new Factory();
        factory.Classifier.Error = failure switch
        {
            "malformed" => new JsonException("sensitive-upstream-detail"),
            "configuration" => new InvalidOperationException("sensitive-upstream-detail"),
            _ => new OperationCanceledException("sensitive-upstream-detail")
        };
        using var client = factory.CreateClient();
        using var response = await client.PostAsJsonAsync("/analyze", new AnalyzeRequest("Action", "Guideline"));
        Assert.Equal(expectedStatus, (int)response.StatusCode);
        Assert.Equal("application/problem+json", response.Content.Headers.ContentType!.MediaType);
        Assert.DoesNotContain("sensitive-upstream-detail", await response.Content.ReadAsStringAsync());
        Assert.Equal(1, factory.Classifier.Calls);
        Assert.Empty((await client.GetFromJsonAsync<AnalyzeResponse[]>("/history"))!);
    }

    [Fact]
    public async Task CallerCancellationReachesClassifierAndDoesNotPersist()
    {
        using var factory = new Factory();
        var started = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var cancelled = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        factory.Classifier.Behavior = async token =>
        {
            using var registration = token.Register(() => cancelled.TrySetResult());
            started.TrySetResult();
            await Task.Delay(Timeout.Infinite, token);
            return new("COMPLIES", .9);
        };
        using var client = factory.CreateClient();
        using var cancellation = new CancellationTokenSource();
        var pending = client.PostAsJsonAsync("/analyze", new AnalyzeRequest("Action", "Guideline"), cancellation.Token);
        try
        {
            await started.Task.WaitAsync(TimeSpan.FromSeconds(5));
            cancellation.Cancel();
            await cancelled.Task.WaitAsync(TimeSpan.FromSeconds(5));
            await Assert.ThrowsAnyAsync<OperationCanceledException>(() => pending.WaitAsync(TimeSpan.FromSeconds(5)));
            Assert.Equal(1, factory.Classifier.Calls);
            Assert.Empty((await client.GetFromJsonAsync<AnalyzeResponse[]>("/history"))!);
        }
        finally { cancellation.Cancel(); }
    }

    [Theory]
    [InlineData(null, null, 415)]
    [InlineData("text/plain", null, 415)]
    [InlineData("application/xml", null, 415)]
    [InlineData("application/json; charset=not-a-real-charset", null, 400)]
    [InlineData("application/json", "gzip", 415)]
    public async Task UnsupportedRequestHeadersAreRejectedBeforeInference(
        string? contentType, string? contentEncoding, int expectedStatus)
    {
        using var factory = new Factory();
        using var client = factory.CreateClient();
        using var request = new HttpRequestMessage(HttpMethod.Post, "/analyze")
        {
            Content = new StringContent("{\"action\":\"Action\",\"guideline\":\"Guideline\"}")
        };
        request.Content.Headers.Remove("Content-Type");
        if (contentType is not null) request.Content.Headers.TryAddWithoutValidation("Content-Type", contentType);
        if (contentEncoding is not null) request.Content.Headers.ContentEncoding.Add(contentEncoding);
        using var response = await client.SendAsync(request);
        Assert.Equal(expectedStatus, (int)response.StatusCode);
        Assert.Equal(0, factory.Classifier.Calls);
        Assert.Empty((await client.GetFromJsonAsync<AnalyzeResponse[]>("/history"))!);

        // A rejected request must not prevent the next valid request from succeeding.
        using var recovered = await client.PostAsJsonAsync("/analyze", new AnalyzeRequest("Action", "Guideline"));
        Assert.Equal(HttpStatusCode.OK, recovered.StatusCode);
        Assert.Equal(1, factory.Classifier.Calls);
    }

    [Theory]
    [InlineData("application/json", null)]
    [InlineData("application/json; charset=utf-8", "identity")]
    [InlineData("application/json; charset=\"utf-8\"", null)]
    public async Task SupportedHeadersStillAllowValidAnalysis(string contentType, string? encoding)
    {
        using var factory = new Factory();
        using var client = factory.CreateClient();
        using var content = new StringContent("{\"action\":\"Action\",\"guideline\":\"Guideline\"}");
        content.Headers.Remove("Content-Type");
        content.Headers.TryAddWithoutValidation("Content-Type", contentType);
        if (encoding is not null) content.Headers.ContentEncoding.Add(encoding);
        using var response = await client.PostAsync("/analyze", content);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal(1, factory.Classifier.Calls);
    }

    [Theory]
    [InlineData("unresponsive", 504)]
    [InlineData("body-stalled", 504)]
    [InlineData("body-disconnected", 502)]
    [InlineData("invalid-json", 502)]
    [InlineData("missing-data", 502)]
    [InlineData("missing-score", 502)]
    [InlineData("rate-limited", 502)]
    [InlineData("unavailable", 502)]
    public async Task RealClassifierFailureDoesNotPersistAndNextRequestRecovers(string failure, int expectedStatus)
    {
        using var upstream = new RecoveringUpstream(failure);
        using var factory = new Factory(upstream: upstream);
        using var client = factory.CreateClient();
        using var response = await client.PostAsJsonAsync("/analyze", new AnalyzeRequest("Restarted servers", "Servers must restart"));
        Assert.Equal(expectedStatus, (int)response.StatusCode);
        Assert.Equal("application/problem+json", response.Content.Headers.ContentType!.MediaType);
        Assert.DoesNotContain("private-upstream-detail", await response.Content.ReadAsStringAsync());
        Assert.Equal(1, upstream.Calls);
        Assert.Empty((await client.GetFromJsonAsync<AnalyzeResponse[]>("/history"))!);
        Assert.Equal(0, (await client.GetFromJsonAsync<SummaryResponse>("/summary"))!.Total);

        // No retry occurs automatically. A separate, explicitly submitted request recovers.
        using var recovered = await client.PostAsJsonAsync("/analyze", new AnalyzeRequest("Restarted servers", "Servers must restart"));
        Assert.Equal(HttpStatusCode.OK, recovered.StatusCode);
        Assert.Equal(2, upstream.Calls);
        Assert.Single((await client.GetFromJsonAsync<AnalyzeResponse[]>("/history"))!);
        Assert.Equal(1, (await client.GetFromJsonAsync<SummaryResponse>("/summary"))!.Total);
    }

    private sealed class RecoveringUpstream(string failure) : HttpMessageHandler
    {
        public int Calls { get; private set; }
        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Calls++;
            Assert.Equal("Bearer", request.Headers.Authorization!.Scheme);
            Assert.Equal("application/json", request.Content!.Headers.ContentType!.MediaType);
            if (Calls == 1)
            {
                if (failure == "unresponsive") await Task.Delay(Timeout.Infinite, cancellationToken);
                if (failure is "body-stalled" or "body-disconnected")
                    return new(HttpStatusCode.OK) { Content = new FailedBody(failure == "body-stalled") };
                var status = failure switch
                {
                    "rate-limited" => HttpStatusCode.TooManyRequests,
                    "unavailable" => HttpStatusCode.ServiceUnavailable,
                    _ => HttpStatusCode.OK
                };
                var body = failure switch
                {
                    "missing-data" => "[]",
                    "missing-score" => "[{\"label\":\"The reported action establishes that Servers restart\"}]",
                    _ => "private-upstream-detail"
                };
                return new(status) { Content = new StringContent(body) };
            }
            return new(HttpStatusCode.OK)
            {
                Content = JsonContent.Create(new[] { new { label = "The reported action establishes that Servers restart", score = .9 } })
            };
        }
    }

    private sealed class FailedBody(bool stall) : HttpContent
    {
        protected override bool TryComputeLength(out long length) { length = 0; return false; }
        protected override Task SerializeToStreamAsync(Stream stream, TransportContext? context)
            => SerializeToStreamAsync(stream, context, CancellationToken.None);
        protected override async Task SerializeToStreamAsync(Stream stream, TransportContext? context, CancellationToken token)
        {
            if (stall) await Task.Delay(Timeout.Infinite, token);
            throw new IOException("private-upstream-detail");
        }
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
        public int Calls { get; private set; }
        public Func<CancellationToken, Task<ClassificationResult>>? Behavior { get; set; }
        public Task<ClassificationResult> ClassifyAsync(string action, string guideline, CancellationToken cancellationToken = default)
        {
            Calls++;
            if (Behavior is not null) return Behavior(cancellationToken);
            return Error is null ? Task.FromResult(Result) : Task.FromException<ClassificationResult>(Error);
        }
    }

    private sealed class Factory(bool live = false, HttpMessageHandler? upstream = null) : WebApplicationFactory<Program>
    {
        private readonly SqliteConnection connection = new("Data Source=:memory:");
        public StubClassifier Classifier { get; } = new();
        private readonly HttpClient? upstreamClient = upstream is null ? null : new HttpClient(upstream, disposeHandler: false)
        {
            Timeout = TimeSpan.FromMilliseconds(250)
        };
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
                    if (upstreamClient is null) services.AddSingleton<IComplianceClassifier>(Classifier);
                    else services.AddSingleton<IComplianceClassifier>(new HuggingFaceComplianceClassifier(
                        upstreamClient,
                        new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
                        {
                            ["HuggingFace:Token"] = "offline-test-placeholder"
                        }).Build()));
                }
                using var provider = services.BuildServiceProvider();
                using var scope = provider.CreateScope();
                scope.ServiceProvider.GetRequiredService<ComplianceDbContext>().Database.Migrate();
            });
        }
        protected override void Dispose(bool disposing)
        {
            base.Dispose(disposing);
            if (disposing)
            {
                upstreamClient?.Dispose();
                connection.Dispose();
            }
        }
    }
}
