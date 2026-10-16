using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using System.Text.RegularExpressions;
using ComplianceMonitor.Api.Models;

namespace ComplianceMonitor.Api.Services;

public class HuggingFaceComplianceClassifier(
    HttpClient httpClient,
    IConfiguration configuration) : IComplianceClassifier
{
    private const string Url =
        "https://router.huggingface.co/hf-inference/models/facebook/bart-large-mnli";
    private const double EvidenceThreshold = 0.55;

    public async Task<ClassificationResult> ClassifyAsync(
        string action,
        string guideline,
        CancellationToken cancellationToken = default)
    {
        if (!Regex.IsMatch(guideline, @"\b(?:must|shall)\b", RegexOptions.IgnoreCase)
            && Regex.IsMatch(
                guideline.Trim(),
                @"^(?:No (?:applicable )?guidelines? (?:exist|exists|apply)|No applicable policy)(?: for [^.!;\r\n]+)?[.!]?\z",
                RegexOptions.IgnoreCase))
            return new("UNCLEAR", 1.0);

        var requirements = RequirementExtractor.Extract(guideline);
        if (requirements.Length == 0)
            return new("UNCLEAR", 0.0);

        var token = configuration["HuggingFace:Token"];
        if (string.IsNullOrWhiteSpace(token))
            throw new InvalidOperationException("Hugging Face token is not configured.");

        // Independent entailment scores are model evidence scores, not probabilities of compliance.
        var labels = requirements
            .Select(r => $"The reported action establishes that {Regex.Replace(r, @"\s+(?:must|shall)\s+", " ", RegexOptions.IgnoreCase)}")
            .Distinct(StringComparer.Ordinal)
            .ToArray();

        using var request = new HttpRequestMessage(HttpMethod.Post, Url)
        {
            Content = JsonContent.Create(new
            {
                inputs = action,
                parameters = new
                {
                    candidate_labels = labels,
                    hypothesis_template = "{}",
                    multi_label = true
                }
            })
        };

        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);

        using var response = await httpClient.SendAsync(request, cancellationToken);
        response.EnsureSuccessStatusCode();

        using var document = await JsonDocument.ParseAsync(
            await response.Content.ReadAsStreamAsync(cancellationToken),
            cancellationToken: cancellationToken);

        if (document.RootElement.ValueKind != JsonValueKind.Array)
            throw new InvalidOperationException("Unexpected Hugging Face response.");

        var scores = new Dictionary<string, double>(StringComparer.Ordinal);
        foreach (var item in document.RootElement.EnumerateArray())
        {
            if (item.ValueKind != JsonValueKind.Object
                || !item.TryGetProperty("label", out var labelValue) || labelValue.ValueKind != JsonValueKind.String
                || !item.TryGetProperty("score", out var scoreValue) || scoreValue.ValueKind != JsonValueKind.Number
                || !scoreValue.TryGetDouble(out var score) || !double.IsFinite(score) || score < 0 || score > 1
                || labelValue.GetString() is not { } label || !scores.TryAdd(label, score))
                throw new InvalidOperationException("Unexpected Hugging Face response.");
        }

        if (scores.Count != labels.Length || labels.Any(l => !scores.ContainsKey(l)))
            throw new InvalidOperationException("Unexpected Hugging Face response.");

        var weakest = labels.Min(l => scores[l]);

        // Missing recurrence evidence overrides even a high entailment score under the strict policy.
        if (requirements.Any(r => !RequirementExtractor.HasRecurrenceEvidence(r, action)))
            return new("DEVIATES", weakest);

        return new(weakest >= EvidenceThreshold ? "COMPLIES" : "DEVIATES", weakest);
    }
}
