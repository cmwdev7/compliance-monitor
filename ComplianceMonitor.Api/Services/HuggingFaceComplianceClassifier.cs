using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using ComplianceMonitor.Api.Models;

namespace ComplianceMonitor.Api.Services;

public class HuggingFaceComplianceClassifier : IComplianceClassifier
{
    private const string Url =
        "https://router.huggingface.co/hf-inference/models/facebook/bart-large-mnli"\;

    private readonly HttpClient _httpClient;
    private readonly IConfiguration _configuration;

    public HuggingFaceComplianceClassifier(
        HttpClient httpClient,
        IConfiguration configuration)
    {
        _httpClient = httpClient;
        _configuration = configuration;
    }

    public async Task<ClassificationResult> ClassifyAsync(
        string action,
        string guideline,
        CancellationToken cancellationToken = default)
    {
        // No applicable policy means compliance cannot be determined.
        if (guideline.Contains(
                "No guidelines exist",
                StringComparison.OrdinalIgnoreCase))
        {
            return new ClassificationResult("UNCLEAR", 1.0);
        }

        var token = _configuration["HuggingFace:Token"];

        if (string.IsNullOrWhiteSpace(token))
        {
            throw new InvalidOperationException(
                "Hugging Face token is not configured.");
        }

        var payload = new
        {
            inputs = $"Guideline: {guideline}\nReported action: {action}",
            parameters = new
            {
                candidate_labels = new[]
                {
                    "the reported action complies with the guideline",
                    "the reported action violates the guideline",
                    "there is insufficient information to determine compliance"
                },
                multi_label = false
            }
        };

        using var request = new HttpRequestMessage(HttpMethod.Post, Url)
        {
            Content = JsonContent.Create(payload)
        };

        request.Headers.Authorization =
            new AuthenticationHeaderValue("Bearer", token);

        using var response = await _httpClient.SendAsync(
            request,
            cancellationToken);

        response.EnsureSuccessStatusCode();

        using var document = await JsonDocument.ParseAsync(
            await response.Content.ReadAsStreamAsync(cancellationToken),
            cancellationToken: cancellationToken);

        var root = document.RootElement;

        if (root.ValueKind != JsonValueKind.Array ||
            root.GetArrayLength() == 0)
        {
            throw new InvalidOperationException(
                "Unexpected Hugging Face response.");
        }

        var best = root.EnumerateArray()
            .Select(item => new
            {
                Label = item.GetProperty("label").GetString() ?? "",
                Score = item.GetProperty("score").GetDouble()
            })
            .OrderByDescending(item => item.Score)
            .First();

        string result;

        if (best.Score < 0.55)
        {
            result = "UNCLEAR";
        }
        else if (best.Label.Contains("violates"))
        {
            result = "DEVIATES";
        }
        else if (best.Label.Contains("complies"))
        {
            result = "COMPLIES";
        }
        else
        {
            result = "UNCLEAR";
        }

        return new ClassificationResult(result, best.Score);
    }
}
