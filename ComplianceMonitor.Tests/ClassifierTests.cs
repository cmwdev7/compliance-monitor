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

    private static HuggingFaceComplianceClassifier Create(FakeHandler handler) => new(new HttpClient(handler), new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?> { ["HuggingFace:Token"] = "test-placeholder" }).Build());

    private sealed class FakeHandler(double score, HttpStatusCode status = HttpStatusCode.OK, bool empty = false, string? body = null) : HttpMessageHandler
    {
        public string? Input { get; private set; }
        public int LabelCount { get; private set; }
        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            using var json = JsonDocument.Parse(await request.Content!.ReadAsStringAsync(cancellationToken));
            Input = json.RootElement.GetProperty("inputs").GetString();
            var parameters = json.RootElement.GetProperty("parameters");
            Assert.True(parameters.GetProperty("multi_label").GetBoolean());
            Assert.Equal("{}", parameters.GetProperty("hypothesis_template").GetString());
            var labels = parameters.GetProperty("candidate_labels").EnumerateArray().Select(x => x.GetString()).ToArray();
            LabelCount = labels.Length;
            return new HttpResponseMessage(status) { Content = body is null ? JsonContent.Create(empty ? [] : labels.Select(label => new { label, score }).ToArray()) : new StringContent(body) };
        }
    }
}
