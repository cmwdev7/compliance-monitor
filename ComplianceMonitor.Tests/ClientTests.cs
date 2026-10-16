using System.Net;
using System.Text;
using System.Text.Json;
using ComplianceMonitor.Client;

namespace ComplianceMonitor.Tests;

public class ClientTests
{
    [Fact]
    public async Task BrowsingDoesNotSubmitAnalysis()
    {
        var (app, handler, output, _) = Create("2\n3\n4\n");
        Assert.Equal(0, await app.RunAsync([]));
        Assert.Equal(new[] { "GET /history", "GET /summary" }, handler.Requests);
        Assert.Contains("Choose:", output.ToString());
    }

    [Theory]
    [InlineData("")]
    [InlineData("4\n")]
    [InlineData("1\n")]
    [InlineData("1\nAction\nGuideline\nn\n4\n")]
    [InlineData("1\n \nGuideline\n4\n")]
    public async Task ExitIncompleteInvalidOrDeclinedInputSendsNothing(string input)
    {
        var (app, handler, _, _) = Create(input);
        Assert.Equal(0, await app.RunAsync([]));
        Assert.Empty(handler.Requests);
    }

    [Fact]
    public async Task InteractiveAnalysisSubmitsUsersInputOnce()
    {
        var (app, handler, _, _) = Create("1\nMy action\nMy guideline\ny\n4\n");
        Assert.Equal(0, await app.RunAsync([]));
        Assert.Equal(new[] { "POST /analyze" }, handler.Requests);
        using var body = JsonDocument.Parse(handler.Bodies.Single());
        Assert.Equal("My action", body.RootElement.GetProperty("action").GetString());
        Assert.Equal("My guideline", body.RootElement.GetProperty("guideline").GetString());
    }

    [Fact]
    public async Task DemoSubmitsFourSamplesAndReadsBothEndpoints()
    {
        var (app, handler, _, _) = Create("y\n");
        Assert.Equal(0, await app.RunAsync(["--demo", "http://localhost:5432"]));
        Assert.Equal(new[] { "POST /analyze", "POST /analyze", "POST /analyze", "POST /analyze", "GET /history", "GET /summary" }, handler.Requests);
    }

    [Fact]
    public async Task DemoRequiresExplicitConfirmation()
    {
        var (app, handler, _, _) = Create("\n");
        Assert.Equal(0, await app.RunAsync(["--demo"]));
        Assert.Empty(handler.Requests);
    }

    [Theory]
    [InlineData("not-a-url")]
    [InlineData("file:///tmp/test")]
    [InlineData("http://localhost/api")]
    [InlineData("--unknown")]
    public async Task InvalidArgumentsDoNotMakeRequests(string argument)
    {
        var (app, handler, _, error) = Create("");
        Assert.Equal(1, await app.RunAsync([argument]));
        Assert.Empty(handler.Requests);
        Assert.Contains("HTTP or HTTPS", error.ToString());
    }

    [Theory]
    [InlineData("http", "HTTP 502")]
    [InlineData("connection", "Check that the API is running")]
    [InlineData("timeout", "Request timed out")]
    [InlineData("json", "invalid JSON")]
    public async Task DemoStopsAfterFirstFailureWithoutRetry(string failure, string message)
    {
        var (app, handler, _, error) = Create("y\n");
        handler.Failure = failure;
        Assert.Equal(1, await app.RunAsync(["--demo"]));
        Assert.Single(handler.Requests);
        Assert.Contains(message, error.ToString());
    }

    [Fact]
    public async Task CancellationReturnsCancelledExitCodeWithoutRetry()
    {
        var (app, handler, _, error) = Create("y\n");
        handler.Failure = "cancel";
        using var cancellation = new CancellationTokenSource();
        handler.Cancel = cancellation.Cancel;
        Assert.Equal(130, await app.RunAsync(["--demo"], cancellation.Token));
        Assert.Single(handler.Requests);
        Assert.DoesNotContain("timed out", error.ToString());
    }

    private static (ConsoleApplication App, Handler Handler, StringWriter Output, StringWriter Error) Create(string input)
    {
        var handler = new Handler();
        var output = new StringWriter();
        var error = new StringWriter();
        return (new ConsoleApplication(new HttpClient(handler), new StringReader(input), output, error), handler, output, error);
    }

    private sealed class Handler : HttpMessageHandler
    {
        public List<string> Requests { get; } = [];
        public List<string> Bodies { get; } = [];
        public string? Failure { get; set; }
        public Action? Cancel { get; set; }
        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken token)
        {
            Requests.Add($"{request.Method} {request.RequestUri!.AbsolutePath}");
            if (request.Content is not null) Bodies.Add(await request.Content.ReadAsStringAsync(token));
            if (Failure == "cancel") { Cancel!(); token.ThrowIfCancellationRequested(); }
            if (Failure == "connection") throw new HttpRequestException();
            if (Failure == "timeout") throw new TaskCanceledException();
            return new HttpResponseMessage(Failure == "http" ? HttpStatusCode.BadGateway : HttpStatusCode.OK)
            {
                Content = new StringContent(Failure == "json" ? "not json" : "{}", Encoding.UTF8, "application/json")
            };
        }
    }
}
