using System.Net.Mail;
using DoAnLapTrinhWeb.Data;
using DoAnLapTrinhWeb.Models.Designer;
using DoAnLapTrinhWeb.Models.Projects;
using Microsoft.EntityFrameworkCore;

namespace DoAnLapTrinhWeb.Services;

public class ProjectStore
{
    private readonly AppDbContext _db;

    public ProjectStore(AppDbContext db)
    {
        _db = db;
    }

    public async Task<IReadOnlyList<ProjectSummaryViewModel>> GetAccessibleProjectsAsync(string email, CancellationToken cancellationToken = default)
    {
        var normalizedEmail = NormalizeEmail(email);

        var projects = await _db.Projects
            .Where(p => p.OwnerEmail == normalizedEmail ||
                        _db.ProjectCollaborators.Any(c => c.ProjectId == p.Id && c.Email == normalizedEmail))
            .OrderByDescending(p => p.UpdatedAt)
            .ToListAsync(cancellationToken);

        var collaboratorMap = await _db.ProjectCollaborators
            .Where(c => projects.Select(p => p.Id).Contains(c.ProjectId))
            .GroupBy(c => c.ProjectId)
            .ToDictionaryAsync(g => g.Key, g => g.Select(c => c.Email).ToList(), cancellationToken);

        return projects.Select(p => ToSummary(p, normalizedEmail, collaboratorMap.GetValueOrDefault(p.Id) ?? new List<string>())).ToList();
    }

    public async Task<DesignerProject?> GetProjectAsync(string projectId, string email, CancellationToken cancellationToken = default)
    {
        var normalizedEmail = NormalizeEmail(email);

        var project = await _db.Projects.FirstOrDefaultAsync(p => p.Id == projectId, cancellationToken);
        if (project is null)
            return null;

        var collaborators = await _db.ProjectCollaborators
            .Where(c => c.ProjectId == projectId)
            .Select(c => c.Email)
            .ToListAsync(cancellationToken);

        project.CollaboratorEmails = collaborators;

        if (!CanAccess(project, normalizedEmail))
            return null;

        return project;
    }

    public async Task<DesignerProject> CreateProjectAsync(string ownerEmail, string projectName, CancellationToken cancellationToken = default)
    {
        var normalizedOwner = NormalizeEmail(ownerEmail);
        var normalizedName = NormalizeName(projectName);

        var project = new DesignerProject
        {
            Name = normalizedName,
            OwnerEmail = normalizedOwner,
            Schema = new DatabaseSchema { ProjectName = normalizedName },
            CreatedAt = DateTimeOffset.UtcNow,
            UpdatedAt = DateTimeOffset.UtcNow
        };

        _db.Projects.Add(project);
        await _db.SaveChangesAsync(cancellationToken);
        return project;
    }

    public async Task<bool> DeleteProjectAsync(string projectId, string ownerEmail, CancellationToken cancellationToken = default)
    {
        var normalizedOwner = NormalizeEmail(ownerEmail);

        var project = await _db.Projects.FirstOrDefaultAsync(p => p.Id == projectId, cancellationToken);
        if (project is null || !IsOwner(project, normalizedOwner))
            return false;

        var collaborators = await _db.ProjectCollaborators
            .Where(c => c.ProjectId == projectId)
            .ToListAsync(cancellationToken);

        _db.ProjectCollaborators.RemoveRange(collaborators);
        _db.Projects.Remove(project);
        await _db.SaveChangesAsync(cancellationToken);
        return true;
    }

    public async Task<DesignerProject?> SaveSchemaAsync(string projectId, string email, DatabaseSchema schema, CancellationToken cancellationToken = default)
    {
        var normalizedEmail = NormalizeEmail(email);

        var project = await _db.Projects.FirstOrDefaultAsync(p => p.Id == projectId, cancellationToken);
        if (project is null)
            return null;

        var collaborators = await _db.ProjectCollaborators
            .Where(c => c.ProjectId == projectId)
            .Select(c => c.Email)
            .ToListAsync(cancellationToken);

        project.CollaboratorEmails = collaborators;

        if (!CanAccess(project, normalizedEmail))
            return null;

        var projectName = NormalizeName(schema.ProjectName);
        schema.ProjectName = projectName;
        schema.Tables ??= new List<SchemaTable>();
        project.Name = projectName;
        project.Schema = schema;
        project.UpdatedAt = DateTimeOffset.UtcNow;

        await _db.SaveChangesAsync(cancellationToken);
        return project;
    }

    public async Task<DesignerProject?> AddCollaboratorAsync(string projectId, string ownerEmail, string collaboratorEmail, CancellationToken cancellationToken = default)
    {
        var normalizedOwner = NormalizeEmail(ownerEmail);
        var normalizedCollaborator = NormalizeEmail(collaboratorEmail);
        if (!IsValidEmail(normalizedCollaborator))
            return null;

        var project = await _db.Projects.FirstOrDefaultAsync(p => p.Id == projectId, cancellationToken);
        if (project is null || !IsOwner(project, normalizedOwner))
            return null;

        if (string.Equals(project.OwnerEmail, normalizedCollaborator, StringComparison.OrdinalIgnoreCase))
            return project;

        var exists = await _db.ProjectCollaborators
            .AnyAsync(c => c.ProjectId == projectId && c.Email == normalizedCollaborator, cancellationToken);

        if (!exists)
        {
            _db.ProjectCollaborators.Add(new ProjectCollaborator
            {
                ProjectId = projectId,
                Email = normalizedCollaborator
            });
            project.UpdatedAt = DateTimeOffset.UtcNow;
            await _db.SaveChangesAsync(cancellationToken);
        }

        return project;
    }

    public async Task<DesignerProject?> RemoveCollaboratorAsync(string projectId, string ownerEmail, string collaboratorEmail, CancellationToken cancellationToken = default)
    {
        var normalizedOwner = NormalizeEmail(ownerEmail);
        var normalizedCollaborator = NormalizeEmail(collaboratorEmail);

        var project = await _db.Projects.FirstOrDefaultAsync(p => p.Id == projectId, cancellationToken);
        if (project is null || !IsOwner(project, normalizedOwner))
            return null;

        var collaborator = await _db.ProjectCollaborators
            .FirstOrDefaultAsync(c => c.ProjectId == projectId && c.Email == normalizedCollaborator, cancellationToken);

        if (collaborator is not null)
        {
            _db.ProjectCollaborators.Remove(collaborator);
            project.UpdatedAt = DateTimeOffset.UtcNow;
            await _db.SaveChangesAsync(cancellationToken);
        }

        return project;
    }

    private static ProjectSummaryViewModel ToSummary(DesignerProject project, string viewerEmail, List<string> collaboratorEmails)
    {
        return new ProjectSummaryViewModel
        {
            Id = project.Id,
            Name = project.Name,
            OwnerEmail = project.OwnerEmail,
            CollaboratorEmails = collaboratorEmails,
            CreatedAt = project.CreatedAt,
            UpdatedAt = project.UpdatedAt,
            TableCount = project.Schema.Tables.Count,
            IsOwner = IsOwner(project, viewerEmail)
        };
    }

    private static bool CanAccess(DesignerProject project, string email)
    {
        return IsOwner(project, email) ||
               project.CollaboratorEmails.Any(collaborator => string.Equals(collaborator, email, StringComparison.OrdinalIgnoreCase));
    }

    private static bool IsOwner(DesignerProject project, string email)
    {
        return string.Equals(project.OwnerEmail, email, StringComparison.OrdinalIgnoreCase);
    }

    private static string NormalizeEmail(string email)
    {
        return (email ?? string.Empty).Trim().ToLowerInvariant();
    }

    private static string NormalizeName(string name)
    {
        var normalized = (name ?? string.Empty).Trim();
        return string.IsNullOrWhiteSpace(normalized) ? "Untitled project" : normalized;
    }

    private static bool IsValidEmail(string email)
    {
        try
        {
            _ = new MailAddress(email);
            return true;
        }
        catch
        {
            return false;
        }
    }
}
