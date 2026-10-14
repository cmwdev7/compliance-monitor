namespace ComplianceMonitor.Api.Models;

public record AnalyzeResponse(
    string Action,
    string Guideline,
    string Result,
    double Confidence,
    DateTime Timestamp
);
