namespace ComplianceMonitor.Api.Models;

public record SummaryResponse(
    int Total,
    Dictionary<string, int> ByResult
);
