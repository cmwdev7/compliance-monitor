namespace ComplianceMonitor.Api.Models;

public record AnalyzeRequest(
    string Action,
    string Guideline
);
