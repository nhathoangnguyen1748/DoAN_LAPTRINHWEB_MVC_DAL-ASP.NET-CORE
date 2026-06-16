using System.Security.Claims;
using DoAnLapTrinhWeb.Models.Designer;
using DoAnLapTrinhWeb.Models.Projects;
using DoAnLapTrinhWeb.Services;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace DoAnLapTrinhWeb.Controllers;

[Authorize]
public class ProjectsController : Controller
{
    private readonly ProjectStore _projectStore;

    public ProjectsController(ProjectStore projectStore)
    {
        _projectStore = projectStore;
    }

    public async Task<IActionResult> Index(CancellationToken cancellationToken)
    {
        var email = CurrentEmail();
        var projects = await _projectStore.GetAccessibleProjectsAsync(email, cancellationToken);
        return View(new ProjectsIndexViewModel
        {
            UserEmail = email,
            Projects = projects
        });
    }

    [HttpPost]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> Create(string name, CancellationToken cancellationToken)
    {
        var project = await _projectStore.CreateProjectAsync(CurrentEmail(), name, cancellationToken);
        return RedirectToAction("Index", "DatabaseDesigner", new { projectId = project.Id });
    }

    [HttpPost]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> Delete(string id, CancellationToken cancellationToken)
    {
        await _projectStore.DeleteProjectAsync(id, CurrentEmail(), cancellationToken);
        return RedirectToAction(nameof(Index));
    }

    [HttpPost]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> AddCollaborator(string id, string email, CancellationToken cancellationToken)
    {
        await _projectStore.AddCollaboratorAsync(id, CurrentEmail(), email, cancellationToken);
        return RedirectToAction(nameof(Index));
    }

    [HttpPost]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> RemoveCollaborator(string id, string email, CancellationToken cancellationToken)
    {
        await _projectStore.RemoveCollaboratorAsync(id, CurrentEmail(), email, cancellationToken);
        return RedirectToAction(nameof(Index));
    }

    [HttpPost("Projects/{id}/schema")]
    public async Task<IActionResult> SaveSchema(string id, [FromBody] DatabaseSchema schema, CancellationToken cancellationToken)
    {
        var project = await _projectStore.SaveSchemaAsync(id, CurrentEmail(), schema, cancellationToken);
        if (project is null)
        {
            return Forbid();
        }

        return Json(new
        {
            success = true,
            projectId = project.Id,
            name = project.Name,
            updatedAt = project.UpdatedAt
        });
    }

    private string CurrentEmail()
    {
        return User.FindFirstValue(ClaimTypes.Email) ??
               User.FindFirstValue("email") ??
               string.Empty;
    }
}
