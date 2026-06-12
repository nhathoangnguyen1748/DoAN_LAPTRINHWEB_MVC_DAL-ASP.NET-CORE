namespace DoAnLapTrinhWeb.Models.Designer;

public class AiReviewResult
{
    public string Summary { get; set; } = "Schema review completed.";
    public string Source { get; set; } = "Deterministic database rules";
    public List<ReviewIssue> Issues { get; set; } = new();
    public int HighCount => Issues.Count(issue => issue.Severity.Equals("HIGH", StringComparison.OrdinalIgnoreCase));
    public int MediumCount => Issues.Count(issue => issue.Severity.Equals("MEDIUM", StringComparison.OrdinalIgnoreCase));
    public int LowCount => Issues.Count(issue => issue.Severity.Equals("LOW", StringComparison.OrdinalIgnoreCase));
}
