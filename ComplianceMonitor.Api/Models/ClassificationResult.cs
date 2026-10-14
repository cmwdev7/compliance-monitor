namespace ComplianceMonitor.Api.Models;

public record ClassificationResult(
    string Result,
    double Confidence
);
