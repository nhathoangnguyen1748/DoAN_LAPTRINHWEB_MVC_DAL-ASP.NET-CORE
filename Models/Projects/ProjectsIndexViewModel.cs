namespace DoAnLapTrinhWeb.Models.Projects;

public class ProjectsIndexViewModel
{
    public string UserEmail { get; set; } = string.Empty;
    public IReadOnlyList<ProjectSummaryViewModel> Projects { get; set; } = Array.Empty<ProjectSummaryViewModel>();
}
