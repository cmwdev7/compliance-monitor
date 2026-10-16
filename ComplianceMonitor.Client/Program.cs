using System.Net.Http.Json;
using System.Text.Json;

using var client = new HttpClient { BaseAddress = new Uri(args.FirstOrDefault() ?? "http://localhost:5000"), Timeout = TimeSpan.FromSeconds(90) };
var cases = new[]
{
    new { action = "Closed ticket #48219 and sent confirmation email", guideline = "All closed tickets must include a confirmation email" },
    new { action = "Closed ticket #48219 without sending confirmation email", guideline = "All closed tickets must include a confirmation email" },
    new { action = "Rebooted the server and checked logs", guideline = "Servers must be rebooted weekly and logs reviewed after restart" },
    new { action = "Skipped torque confirmation at Station 3", guideline = "No guidelines exist for this case." }
};
foreach (var sample in cases)
    await Display("POST /analyze", () => client.PostAsJsonAsync("/analyze", sample));
await Display("GET /history", () => client.GetAsync("/history"));
await Display("GET /summary", () => client.GetAsync("/summary"));

async Task Display(string title, Func<Task<HttpResponseMessage>> send)
{
    Console.WriteLine(title);
    try
    {
        using var response = await send();
        if (!response.IsSuccessStatusCode)
        {
            Console.Error.WriteLine($"HTTP {(int)response.StatusCode}: {response.ReasonPhrase}");
            Environment.ExitCode = 1;
            return;
        }
        using var json = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        Console.WriteLine(JsonSerializer.Serialize(json.RootElement, new JsonSerializerOptions { WriteIndented = true }));
    }
    catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException or JsonException)
    {
        Console.Error.WriteLine(ex is TaskCanceledException ? "Request timed out." : "Request failed or returned invalid JSON.");
        Environment.ExitCode = 1;
    }
}
