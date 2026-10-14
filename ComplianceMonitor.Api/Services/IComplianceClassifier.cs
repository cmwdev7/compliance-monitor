using ComplianceMonitor.Api.Models;

namespace ComplianceMonitor.Api.Services;

public interface IComplianceClassifier
{
    Task<ClassificationResult> ClassifyAsync(
        string action,
        string guideline,
        CancellationToken cancellationToken = default);
}
