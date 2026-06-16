namespace DoAnLapTrinhWeb.Models.Projects;

public class ProjectSummaryViewModel
{
    public string Id { get; set; } = string.Empty;
    public string Name { get; set; } = string.Empty;
    public string OwnerEmail { get; set; } = string.Empty;
    public List<string> CollaboratorEmails { get; set; } = new();
    public DateTimeOffset CreatedAt { get; set; }
    public DateTimeOffset UpdatedAt { get; set; }
    public int TableCount { get; set; }
    public bool IsOwner { get; set; }
}
