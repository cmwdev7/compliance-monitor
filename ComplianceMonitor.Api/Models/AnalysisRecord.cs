namespace ComplianceMonitor.Api.Models;

public class AnalysisRecord
{
    public int Id { get; set; }

    public string Action { get; set; } = string.Empty;

    public string Guideline { get; set; } = string.Empty;

    public string Result { get; set; } = string.Empty;

    public double Confidence { get; set; }

    public DateTime Timestamp { get; set; }
}
