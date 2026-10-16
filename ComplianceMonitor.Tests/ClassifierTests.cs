using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using ComplianceMonitor.Api.Services;
using Microsoft.Extensions.Configuration;

namespace ComplianceMonitor.Tests;

public class ClassifierTests
{
    [Theory]
    [InlineData("Closed ticket #48219 and sent confirmation email", "All closed tickets must include a confirmation email", .6750, "COMPLIES")]
    [InlineData("Closed ticket #48219 without sending confirmation email", "All closed tickets must include a confirmation email", .0013, "DEVIATES")]
    [InlineData("Rebooted the server and checked logs", "Servers must be rebooted weekly and logs reviewed after restart", .0954, "DEVIATES")]
    [InlineData("Closed ticket and sent confirmation email", "All closed tickets must include a confirmation email", .95, "COMPLIES")]
    [InlineData("Closed ticket without sending confirmation email", "All closed tickets must include a confirmation email", .1, "DEVIATES")]
    [InlineData("Rebooted the server and checked logs", "Servers must be rebooted weekly and logs reviewed after restart", .99, "DEVIATES")]
    [InlineData("Rebooted weekly and reviewed logs after restart", "Servers must be rebooted weekly and logs reviewed after restart", .95, "COMPLIES")]
    [InlineData("Checked equipment", "Equipment must be inspected daily", .99, "DEVIATES")]
    [InlineData("Checked equipment", "Equipment must be inspected every 2 months", .99, "DEVIATES")]
    [InlineData("Inspected equipment daily", "Equipment must be inspected daily", .5, "DEVIATES")]
    public async Task AppliesStrictPolicy(string action, string guideline, double score, string expected)
    {
        var handler = new FakeHandler(score);
        var result = await Create(handler).ClassifyAsync(action, guideline);
        Assert.Equal(expected, result.Result);
        Assert.Equal(score, result.Confidence);
        Assert.Equal(action, handler.Input);
        Assert.Equal(RequirementExtractor.Extract(guideline).Length, handler.LabelCount);
    }

    [Theory]
    [InlineData("No guidelines exist for this case.")]
    [InlineData("No applicable guidelines apply.")]
    [InlineData("Servers should perhaps restart")]
    [InlineData("Servers must restart unless busy")]
    public async Task UnevaluableGuidelinesSkipInference(string guideline)
    {
        var handler = new FakeHandler(.99);
        Assert.Equal("UNCLEAR", (await Create(handler).ClassifyAsync("An action", guideline)).Result);
        Assert.Null(handler.Input);
    }

    [Fact]
    public void ExtractsCompoundWithoutSplittingObjectLists()
    {
        Assert.Equal(new[] { "Servers must be rebooted weekly", "logs must be reviewed after restart" }, RequirementExtractor.Extract("Servers must be rebooted weekly and logs reviewed after restart"));
        Assert.Single(RequirementExtractor.Extract("Reports must include name and address"));
    }

    [Theory]
    [InlineData("Reports must include name and address and supervisors must review reports", "supervisors must review reports")]
    [InlineData("Reports must include name and address and signatures verified", "signatures must be verified")]
    public void PreservesObjectListBeforeAnotherRequirement(string guideline, string secondRequirement)
    {
        Assert.Equal(new[] { "Reports must include name and address", secondRequirement }, RequirementExtractor.Extract(guideline));
    }

    [Theory]
    [InlineData(.9, "COMPLIES")]
    [InlineData(.1, "DEVIATES")]
    public async Task RepeatedRequirementsUseOneCandidateLabel(double score, string expected)
    {
        var handler = new FakeHandler(score);
        var result = await Create(handler).ClassifyAsync("Restarted servers", "Servers must restart. Servers must restart.");
        Assert.Equal(expected, result.Result);
        Assert.Equal(score, result.Confidence);
        Assert.Equal(1, handler.LabelCount);
    }

    [Fact]
    public async Task PropagatesHttpFailure() => await Assert.ThrowsAsync<HttpRequestException>(() => Create(new FakeHandler(.9, HttpStatusCode.PaymentRequired)).ClassifyAsync("Action", "Servers must restart"));

    [Fact]
    public async Task RejectsMissingLabels() => await Assert.ThrowsAsync<InvalidOperationException>(() => Create(new FakeHandler(.9, empty: true)).ClassifyAsync("Action", "Servers must restart"));

    [Theory]
    [InlineData("{}")]
    [InlineData("[null]")]
    [InlineData("[{\"label\":12,\"score\":0.9}]")]
    [InlineData("[{\"label\":\"unexpected\",\"score\":0.9}]")]
    [InlineData("[{\"label\":\"The reported action establishes that Servers restart\",\"score\":\"0.9\"}]")]
    [InlineData("[{\"label\":\"The reported action establishes that Servers restart\",\"score\":1.1}]")]
    [InlineData("[{\"label\":\"The reported action establishes that Servers restart\",\"score\":-0.1}]")]
    [InlineData("[{\"label\":\"The reported action establishes that Servers restart\"}]")]
    [InlineData("[{\"label\":\"The reported action establishes that Servers restart\",\"score\":0.9},{\"label\":\"The reported action establishes that Servers restart\",\"score\":0.8}]")]
    public async Task RejectsMalformedResponseContracts(string body)
    {
        await Assert.ThrowsAsync<InvalidOperationException>(() => Create(new FakeHandler(.9, body: body)).ClassifyAsync("Action", "Servers must restart"));
    }

    [Theory]
    [InlineData(.95, .30, "DEVIATES", .30, false)]
    [InlineData(.30, .95, "DEVIATES", .30, false)]
    [InlineData(.95, .70, "COMPLIES", .70, false)]
    [InlineData(.95, .55, "COMPLIES", .55, false)]
    [InlineData(.95, .5499, "DEVIATES", .5499, false)]
    [InlineData(.95, .30, "DEVIATES", .30, true)]
    [InlineData(.30, .95, "DEVIATES", .30, true)]
    [InlineData(.95, .70, "COMPLIES", .70, true)]
    public async Task UsesWeakestRequirementRegardlessOfResponseOrder(
        double first, double second, string expected, double weakest, bool reverse)
    {
        var handler = new FakeHandler(.9, perLabelScores: [first, second], reverse: reverse);
        var result = await Create(handler).ClassifyAsync(
            "Restarted servers and reviewed logs", "Servers must restart. Operators must review logs.");
        Assert.Equal(2, handler.LabelCount);
        Assert.Equal(expected, result.Result);
        Assert.Equal(weakest, result.Confidence);
    }

    [Fact]
    public async Task RecurrenceOverrideRetainsWeakestOfDifferentScores()
    {
        var result = await Create(new FakeHandler(.9, perLabelScores: [.99, .85])).ClassifyAsync(
            "Restarted servers and reviewed logs", "Servers must restart weekly. Operators must review logs.");
        Assert.Equal("DEVIATES", result.Result);
        Assert.Equal(.85, result.Confidence);
    }

    [Fact]
    public async Task MissingTokenFailsBeforeSending()
    {
        var handler = new FakeHandler(.9);
        var classifier = new HuggingFaceComplianceClassifier(new HttpClient(handler), new ConfigurationBuilder().Build());
        await Assert.ThrowsAsync<InvalidOperationException>(() => classifier.ClassifyAsync("Action", "Servers must restart"));
        Assert.Equal(0, handler.Calls);
    }

    [Theory]
    [InlineData("No guidelines exist for this case.")]
    [InlineData("Servers should restart")]
    public async Task LocalUnclearDoesNotRequireToken(string guideline)
    {
        var handler = new FakeHandler(.9);
        var classifier = new HuggingFaceComplianceClassifier(new HttpClient(handler), new ConfigurationBuilder().Build());
        Assert.Equal("UNCLEAR", (await classifier.ClassifyAsync("Action", guideline)).Result);
        Assert.Equal(0, handler.Calls);
    }

    [Theory]
    [InlineData(401)]
    [InlineData(402)]
    [InlineData(429)]
    [InlineData(503)]
    public async Task UpstreamErrorsPropagateWithoutRetry(int status)
    {
        var handler = new FakeHandler(.9, (HttpStatusCode)status);
        var error = await Assert.ThrowsAsync<HttpRequestException>(() => Create(handler).ClassifyAsync("Action", "Servers must restart"));
        Assert.Equal((HttpStatusCode)status, error.StatusCode);
        Assert.Equal(1, handler.Calls);
    }

    [Theory]
    [InlineData("")]
    [InlineData("<html>unavailable</html>")]
    [InlineData("[{\"label\":")]
    public async Task RejectsInvalidJsonWithoutRetry(string body)
    {
        var handler = new FakeHandler(.9, body: body);
        await Assert.ThrowsAnyAsync<JsonException>(() => Create(handler).ClassifyAsync("Action", "Servers must restart"));
        Assert.Equal(1, handler.Calls);
    }

    [Theory]
    [InlineData("[]")]
    [InlineData("null")]
    [InlineData("[{\"label\":null,\"score\":0.9}]")]
    [InlineData("[{\"label\":\"The reported action establishes that Servers restart\",\"score\":null}]")]
    [InlineData("[{\"label\":\"The reported action establishes that Servers restart\",\"score\":1e999}]")]
    public async Task RejectsEmptyNullAndNonfiniteResponses(string body)
    {
        await Assert.ThrowsAsync<InvalidOperationException>(() => Create(new FakeHandler(.9, body: body))
            .ClassifyAsync("Action", "Servers must restart"));
    }

    [Fact]
    public async Task MissingOneOfMultipleRequirementScoresIsRejected()
    {
        const string body = "[{\"label\":\"The reported action establishes that Servers restart\",\"score\":0.9}]";
        await Assert.ThrowsAsync<InvalidOperationException>(() => Create(new FakeHandler(.9, body: body))
            .ClassifyAsync("Action", "Servers must restart. Operators must review logs."));
    }

    [Fact]
    public async Task CallerCancellationReachesHttpHandlerWithoutRetry()
    {
        using var cancellation = new CancellationTokenSource();
        var reached = new TaskCompletionSource<CancellationToken>(TaskCreationOptions.RunContinuationsAsynchronously);
        var observed = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var handler = new FakeHandler(.9, beforeResponse: async token =>
        {
            using var registration = token.Register(() => observed.TrySetResult());
            reached.TrySetResult(token);
            await Task.Delay(Timeout.Infinite, token);
        });
        var pending = Create(handler).ClassifyAsync("Action", "Servers must restart", cancellation.Token);
        try
        {
            Assert.True((await reached.Task.WaitAsync(TimeSpan.FromSeconds(5))).CanBeCanceled);
            cancellation.Cancel();
            await observed.Task.WaitAsync(TimeSpan.FromSeconds(5));
            await Assert.ThrowsAnyAsync<OperationCanceledException>(() => pending.WaitAsync(TimeSpan.FromSeconds(5)));
            Assert.Equal(1, handler.Calls);
        }
        finally { cancellation.Cancel(); }
    }

    private static HuggingFaceComplianceClassifier Create(FakeHandler handler) => new(new HttpClient(handler), new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?> { ["HuggingFace:Token"] = "test-placeholder" }).Build());

    private sealed class FakeHandler(double score, HttpStatusCode status = HttpStatusCode.OK, bool empty = false, string? body = null, double[]? perLabelScores = null, bool reverse = false, Func<CancellationToken, Task>? beforeResponse = null) : HttpMessageHandler
    {
        public string? Input { get; private set; }
        public int LabelCount { get; private set; }
        public int Calls { get; private set; }
        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Calls++;
            if (beforeResponse is not null) await beforeResponse(cancellationToken);
            using var json = JsonDocument.Parse(await request.Content!.ReadAsStringAsync(cancellationToken));
            Input = json.RootElement.GetProperty("inputs").GetString();
            var parameters = json.RootElement.GetProperty("parameters");
            Assert.True(parameters.GetProperty("multi_label").GetBoolean());
            Assert.Equal("{}", parameters.GetProperty("hypothesis_template").GetString());
            var labels = parameters.GetProperty("candidate_labels").EnumerateArray().Select(x => x.GetString()).ToArray();
            LabelCount = labels.Length;
            var results = labels.Select((label, index) => new { label, score = perLabelScores?[index] ?? score }).ToArray();
            if (reverse) Array.Reverse(results);
            return new HttpResponseMessage(status)
            {
                Content = body is null ? JsonContent.Create(empty ? [] : results) : new StringContent(body)
            };
        }
    }
}
