namespace DoAnLapTrinhWeb.Models.Projects;

public class ProjectCollaborator
{
    public int Id { get; set; }
    public string ProjectId { get; set; } = string.Empty;
    public string Email { get; set; } = string.Empty;
}
