using DoAnLapTrinhWeb.Models.Designer;

namespace DoAnLapTrinhWeb.Models.Projects;

public class DesignerWorkspaceViewModel
{
    public string UserEmail { get; set; } = string.Empty;
    public string? CurrentProjectId { get; set; }
    public string CurrentProjectName { get; set; } = "Unsaved project";
    public bool CanManageCurrentProject { get; set; }
    public DatabaseSchema Schema { get; set; } = new();
    public IReadOnlyList<ProjectSummaryViewModel> Projects { get; set; } = Array.Empty<ProjectSummaryViewModel>();
    public IReadOnlyList<string> CollaboratorEmails { get; set; } = Array.Empty<string>();
}
