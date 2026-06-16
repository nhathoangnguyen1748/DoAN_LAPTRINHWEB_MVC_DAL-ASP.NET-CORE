using System.Security.Claims;
using DoAnLapTrinhWeb.Models.Designer;
using DoAnLapTrinhWeb.Models.Projects;
using DoAnLapTrinhWeb.Services;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace DoAnLapTrinhWeb.Controllers;

[Authorize]
public class DatabaseDesignerController : Controller
{
    private readonly SqlSchemaParser _parser;
    private readonly SchemaReviewService _reviewService;
    private readonly AspNetMvcExportService _exportService;
    private readonly ProjectStore _projectStore;

    public DatabaseDesignerController(
        SqlSchemaParser parser,
        SchemaReviewService reviewService,
        AspNetMvcExportService exportService,
        ProjectStore projectStore)
    {
        _parser = parser;
        _reviewService = reviewService;
        _exportService = exportService;
        _projectStore = projectStore;
    }

    public async Task<IActionResult> Index(string? projectId, CancellationToken cancellationToken)
    {
        var email = CurrentEmail();
        var projects = await _projectStore.GetAccessibleProjectsAsync(email, cancellationToken);
        DesignerProject? currentProject = null;

        if (!string.IsNullOrWhiteSpace(projectId))
        {
            currentProject = await _projectStore.GetProjectAsync(projectId, email, cancellationToken);
            if (currentProject is null)
            {
                return Forbid();
            }
        }
        else if (projects.Count > 0)
        {
            currentProject = await _projectStore.GetProjectAsync(projects[0].Id, email, cancellationToken);
        }

        return View(new DesignerWorkspaceViewModel
        {
            UserEmail = email,
            CurrentProjectId = currentProject?.Id,
            CurrentProjectName = currentProject?.Name ?? "Unsaved project",
            CanManageCurrentProject = currentProject is not null &&
                                      string.Equals(currentProject.OwnerEmail, email, StringComparison.OrdinalIgnoreCase),
            Schema = currentProject?.Schema ?? new DatabaseSchema { ProjectName = "GeneratedMvcApp" },
            Projects = projects,
            CollaboratorEmails = currentProject?.CollaboratorEmails ?? new List<string>()
        });
    }

    [HttpPost]
    public IActionResult ImportSql([FromBody] SqlImportRequest request)
    {
        var schema = _parser.Parse(request.Sql, request.ProjectName);
        return Json(schema);
    }

    [HttpPost]
    public async Task<IActionResult> Review([FromBody] DatabaseSchema schema, CancellationToken cancellationToken)
    {
        var result = await _reviewService.ReviewAsync(schema, cancellationToken);
        return Json(result);
    }

    [HttpPost]
    public async Task<IActionResult> FixReview([FromBody] ReviewFixRequest request, CancellationToken cancellationToken)
    {
        var result = await _reviewService.FixAsync(request, cancellationToken);
        return Json(result);
    }

    [HttpPost]
    public IActionResult Export([FromBody] DatabaseSchema schema)
    {
        if (schema.Tables.Count == 0)
        {
            return BadRequest(new { message = "Add or import at least one table before exporting." });
        }

        var projectName = string.IsNullOrWhiteSpace(schema.ProjectName) ? "GeneratedMvcApp" : schema.ProjectName.Trim();
        var zip = _exportService.CreateZip(schema);
        return File(zip, "application/zip", $"{projectName}-aspnet-mvc-sqlserver.zip");
    }

    private string CurrentEmail()
    {
        return User.FindFirstValue(ClaimTypes.Email) ??
               User.FindFirstValue("email") ??
               string.Empty;
    }
}
