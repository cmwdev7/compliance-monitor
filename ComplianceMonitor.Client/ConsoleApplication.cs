using System.Net.Http.Json;
using System.Text.Json;

namespace ComplianceMonitor.Client;

public sealed class ConsoleApplication(HttpClient http, TextReader input, TextWriter output, TextWriter error)
{
    public async Task<int> RunAsync(string[] args, CancellationToken cancellationToken = default)
    {
        if (args.Contains("--help"))
        {
            output.WriteLine("Usage: ComplianceMonitor.Client [http://localhost:5000] [--demo]");
            return 0;
        }
        var addresses = args.Where(a => a != "--demo").ToArray();
        if (addresses.Length > 1 || !Uri.TryCreate(addresses.FirstOrDefault() ?? "http://localhost:5000", UriKind.Absolute, out var address)
            || (address.Scheme != "http" && address.Scheme != "https") || !string.IsNullOrEmpty(address.UserInfo)
            || !string.IsNullOrEmpty(address.Query) || !string.IsNullOrEmpty(address.Fragment)
            || address.AbsolutePath != "/")
        {
            error.WriteLine("Provide one HTTP or HTTPS API URL, such as http://localhost:5000, and optionally --demo.");
            return 1;
        }
        http.BaseAddress = address;
        try
        {
            if (args.Contains("--demo")) return await RunDemoAsync(cancellationToken);
            output.WriteLine($"Compliance Monitor — {address}");
            while (true)
            {
                output.WriteLine("1. Analyze an action (uses inference credits)\n2. View history\n3. View summary\n4. Exit");
                output.Write("Choose: ");
                var choice = (await input.ReadLineAsync(cancellationToken))?.Trim();
                if (choice is null or "4") return 0;
                switch (choice)
                {
                    case "1":
                        output.Write("Action: ");
                        var action = await input.ReadLineAsync(cancellationToken);
                        if (action is null) return 0;
                        output.Write("Guideline: ");
                        var guideline = await input.ReadLineAsync(cancellationToken);
                        if (guideline is null) return 0;
                        if (string.IsNullOrWhiteSpace(action) || string.IsNullOrWhiteSpace(guideline))
                        {
                            error.WriteLine("Action and guideline are required.");
                            break;
                        }
                        if (await ConfirmAsync("Analysis may use inference credits and creates a record on success.", cancellationToken))
                            await DisplayAsync("POST /analyze", () => http.PostAsJsonAsync("/analyze", new { action, guideline }, cancellationToken), cancellationToken);
                        break;
                    case "2":
                        await DisplayAsync("GET /history", () => http.GetAsync("/history", cancellationToken), cancellationToken);
                        break;
                    case "3":
                        await DisplayAsync("GET /summary", () => http.GetAsync("/summary", cancellationToken), cancellationToken);
                        break;
                    default: output.WriteLine("Choose 1, 2, 3, or 4."); break;
                }
            }
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            output.WriteLine("Cancelled. A submitted analysis may still have used credits or been saved; check history before resubmitting.");
            return 130;
        }
    }

    private async Task<bool> ConfirmAsync(string message, CancellationToken token)
    {
        output.WriteLine(message);
        output.Write("Continue? [y/N]: ");
        return string.Equals((await input.ReadLineAsync(token))?.Trim(), "y", StringComparison.OrdinalIgnoreCase);
    }

    private async Task<int> RunDemoAsync(CancellationToken token)
    {
        if (!await ConfirmAsync("Demo submits four sample analyses (normally three paid inference requests), then reads history and summary. Successful analyses add records.", token))
            return 0;
        var cases = new[]
        {
            new { action = "Closed ticket #48219 and sent confirmation email", guideline = "All closed tickets must include a confirmation email" },
            new { action = "Closed ticket #48219 without sending confirmation email", guideline = "All closed tickets must include a confirmation email" },
            new { action = "Rebooted the server and checked logs", guideline = "Servers must be rebooted weekly and logs reviewed after restart" },
            new { action = "Skipped torque confirmation at Station 3", guideline = "No guidelines exist for this case." }
        };
        foreach (var sample in cases)
            if (!await DisplayAsync("POST /analyze", () => http.PostAsJsonAsync("/analyze", sample, token), token)) return 1;
        if (!await DisplayAsync("GET /history", () => http.GetAsync("/history", token), token)) return 1;
        return await DisplayAsync("GET /summary", () => http.GetAsync("/summary", token), token) ? 0 : 1;
    }

    private async Task<bool> DisplayAsync(string title, Func<Task<HttpResponseMessage>> send, CancellationToken token)
    {
        output.WriteLine(title);
        try
        {
            using var response = await send();
            if (!response.IsSuccessStatusCode)
            {
                error.WriteLine($"HTTP {(int)response.StatusCode}: {response.ReasonPhrase}. Check the API terminal for details.");
                return false;
            }
            using var json = JsonDocument.Parse(await response.Content.ReadAsStringAsync(token));
            output.WriteLine(JsonSerializer.Serialize(json.RootElement, new JsonSerializerOptions { WriteIndented = true }));
            return true;
        }
        catch (HttpRequestException)
        {
            error.WriteLine($"Could not complete the request to {http.BaseAddress}. Check that the API is running at this address. A submitted analysis may have been processed; check history before resubmitting.");
        }
        catch (OperationCanceledException) when (!token.IsCancellationRequested)
        {
            error.WriteLine("Request timed out. A submitted analysis may still have used credits or been saved; check history before resubmitting.");
        }
        catch (JsonException)
        {
            error.WriteLine("The API returned invalid JSON. A submitted analysis may have been saved; check history before resubmitting.");
        }
        return false;
    }
}
