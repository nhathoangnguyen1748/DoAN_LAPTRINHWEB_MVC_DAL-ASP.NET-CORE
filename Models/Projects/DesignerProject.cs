using DoAnLapTrinhWeb.Models.Designer;

namespace DoAnLapTrinhWeb.Models.Projects;

public class DesignerProject
{
    public string Id { get; set; } = Guid.NewGuid().ToString("N");
    public string Name { get; set; } = "Untitled project";
    public string OwnerEmail { get; set; } = string.Empty;
    public List<string> CollaboratorEmails { get; set; } = new();
    public DatabaseSchema Schema { get; set; } = new();
    public DateTimeOffset CreatedAt { get; set; } = DateTimeOffset.UtcNow;
    public DateTimeOffset UpdatedAt { get; set; } = DateTimeOffset.UtcNow;
}
