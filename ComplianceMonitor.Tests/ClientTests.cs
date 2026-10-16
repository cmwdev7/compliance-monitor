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
    [InlineData("https://user:password@example.com")]
    [InlineData("http://localhost:5000?secret=value")]
    [InlineData("http://localhost:5000#fragment")]
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

    [Fact]
    public async Task HelpDoesNotConnectOrPrompt()
    {
        var (app, handler, output, _) = Create("");
        Assert.Equal(0, await app.RunAsync(["--help"]));
        Assert.Empty(handler.Requests);
        Assert.Contains("Usage:", output.ToString());
    }

    [Fact]
    public async Task MultipleAddressesAreRejected()
    {
        var (app, handler, _, _) = Create("");
        Assert.Equal(1, await app.RunAsync(["http://localhost:5000", "http://localhost:5001"]));
        Assert.Empty(handler.Requests);
    }

    [Fact]
    public async Task InvalidMenuChoicesDoNotSubmitAnything()
    {
        var (app, handler, output, _) = Create("wrong\n5\n4\n");
        Assert.Equal(0, await app.RunAsync([]));
        Assert.Empty(handler.Requests);
        Assert.Contains("Choose 1, 2, 3, or 4.", output.ToString());
    }

    [Theory]
    [InlineData("1\nAction\n\n4\n")]
    [InlineData("1\nAction\nGuideline\n")]
    [InlineData("1\nAction\nGuideline\nyes please\n4\n")]
    public async Task MissingInputOrUnrecognizedConfirmationDoesNotSubmit(string text)
    {
        var (app, handler, _, _) = Create(text);
        Assert.Equal(0, await app.RunAsync([]));
        Assert.Empty(handler.Requests);
    }

    [Theory]
    [InlineData(400)]
    [InlineData(401)]
    [InlineData(429)]
    [InlineData(500)]
    [InlineData(504)]
    public async Task HttpErrorsShowStatusWithoutDisplayingRawBodyOrRetrying(int status)
    {
        var (app, handler, _, error) = Create("y\n");
        handler.Status = (HttpStatusCode)status;
        handler.Body = "sensitive-upstream-detail";
        Assert.Equal(1, await app.RunAsync(["--demo"]));
        Assert.Single(handler.Requests);
        Assert.Contains($"HTTP {status}", error.ToString());
        Assert.DoesNotContain("sensitive-upstream-detail", error.ToString());
    }

    [Fact]
    public async Task InteractiveMenuRecoversAfterFailureWhenUserChoosesAnotherRequest()
    {
        var (app, handler, output, error) = Create("3\n2\n4\n");
        handler.Failure = "connection";
        handler.FailOnCall = 1;
        Assert.Equal(0, await app.RunAsync([]));
        Assert.Equal(new[] { "GET /summary", "GET /history" }, handler.Requests);
        Assert.Contains("Check that the API is running", error.ToString());
        Assert.Contains("{}", output.ToString());
    }

    [Theory]
    [InlineData("3\n4\n")]
    [InlineData("1\nAction\nGuideline\ny\n4\n")]
    public async Task InvalidJsonReturnsToMenuWithoutAutomaticRetry(string input)
    {
        var (app, handler, _, error) = Create(input);
        handler.Failure = "json";
        Assert.Equal(0, await app.RunAsync([]));
        Assert.Single(handler.Requests);
        Assert.Contains("invalid JSON", error.ToString());
    }

    [Fact]
    public async Task CancellationWhileWaitingForInputMakesNoRequests()
    {
        using var cancellation = new CancellationTokenSource();
        var reader = new WaitingReader();
        var handler = new Handler();
        var output = new StringWriter();
        var error = new StringWriter();
        using var http = new HttpClient(handler);
        var pending = new ConsoleApplication(http, reader, output, error).RunAsync([], cancellation.Token);
        try
        {
            await reader.Waiting.Task.WaitAsync(TimeSpan.FromSeconds(5));
            cancellation.Cancel();
            Assert.Equal(130, await pending.WaitAsync(TimeSpan.FromSeconds(5)));
            Assert.Empty(handler.Requests);
            Assert.Contains("Cancelled.", output.ToString());
            Assert.Empty(error.ToString());
        }
        finally { cancellation.Cancel(); }
    }

    [Theory]
    [InlineData("timeout")]
    [InlineData("json")]
    [InlineData("http")]
    [InlineData("body-disconnected")]
    public async Task InteractiveClientCanBrowseAfterAnalysisFailureWithoutResubmitting(string failure)
    {
        var (app, handler, _, error) = Create("1\nAction\nGuideline\ny\n3\n4\n");
        handler.Failure = failure;
        handler.FailOnCall = 1;
        Assert.Equal(0, await app.RunAsync([]));
        Assert.Equal(new[] { "POST /analyze", "GET /summary" }, handler.Requests);
        Assert.NotEmpty(error.ToString());
    }

    [Fact]
    public async Task ActualHttpTimeoutCancelsAnUnresponsiveRequestWithoutRetry()
    {
        var observed = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        using var handler = new UnresponsiveHandler(observed);
        using var http = new HttpClient(handler) { Timeout = TimeSpan.FromMilliseconds(250) };
        var error = new StringWriter();
        var app = new ConsoleApplication(http, new StringReader("y\n"), new StringWriter(), error);
        Assert.Equal(1, await app.RunAsync(["--demo"]).WaitAsync(TimeSpan.FromSeconds(5)));
        await observed.Task.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.Equal(1, handler.Calls);
        Assert.Contains("timed out", error.ToString());
    }

    private sealed class UnresponsiveHandler(TaskCompletionSource observed) : HttpMessageHandler
    {
        public int Calls { get; private set; }
        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken token)
        {
            Calls++;
            using var registration = token.Register(() => observed.TrySetResult());
            await Task.Delay(Timeout.Infinite, token);
            return new(HttpStatusCode.OK);
        }
    }

    private sealed class DisconnectedBody : HttpContent
    {
        protected override bool TryComputeLength(out long length) { length = 0; return false; }
        protected override Task SerializeToStreamAsync(Stream stream, TransportContext? context)
            => throw new IOException("private-response-detail");
    }

    private sealed class WaitingReader : TextReader
    {
        public TaskCompletionSource Waiting { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public override async ValueTask<string?> ReadLineAsync(CancellationToken cancellationToken)
        {
            Waiting.TrySetResult();
            await Task.Delay(Timeout.Infinite, cancellationToken);
            return null;
        }
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
        public int? FailOnCall { get; set; }
        public HttpStatusCode? Status { get; set; }
        public string? Body { get; set; }
        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken token)
        {
            Requests.Add($"{request.Method} {request.RequestUri!.AbsolutePath}");
            if (request.Content is not null) Bodies.Add(await request.Content.ReadAsStringAsync(token));
            var failure = FailOnCall is null || FailOnCall == Requests.Count ? Failure : null;
            if (failure == "cancel") { Cancel!(); token.ThrowIfCancellationRequested(); }
            if (failure == "connection") throw new HttpRequestException();
            if (failure == "timeout") throw new TaskCanceledException();
            if (failure == "body-disconnected") return new(HttpStatusCode.OK) { Content = new DisconnectedBody() };
            return new HttpResponseMessage(Status ?? (failure == "http" ? HttpStatusCode.BadGateway : HttpStatusCode.OK))
            {
                Content = new StringContent(Body ?? (failure == "json" ? "not json" : "{}"), Encoding.UTF8, "application/json")
            };
        }
    }
}
