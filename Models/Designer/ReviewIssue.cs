namespace DoAnLapTrinhWeb.Models.Designer;

public class ReviewIssue
{
    public string Severity { get; set; } = "LOW";
    public string Type { get; set; } = "GENERAL";
    public string? Table { get; set; }
    public string? Column { get; set; }
    public string Message { get; set; } = string.Empty;
    public string Suggestion { get; set; } = string.Empty;
    public bool CanAutoFix { get; set; }
    public string FixAction { get; set; } = string.Empty;
}
