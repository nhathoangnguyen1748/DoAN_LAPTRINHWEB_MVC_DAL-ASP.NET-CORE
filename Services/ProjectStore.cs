using System.Net.Mail;
using System.Text.Json;
using DoAnLapTrinhWeb.Models.Designer;
using DoAnLapTrinhWeb.Models.Projects;

namespace DoAnLapTrinhWeb.Services;

public class ProjectStore
{
    private readonly string _filePath;
    private readonly SemaphoreSlim _gate = new(1, 1);
    private readonly JsonSerializerOptions _jsonOptions = new(JsonSerializerDefaults.Web)
    {
        WriteIndented = true
    };

    public ProjectStore(IWebHostEnvironment environment)
    {
        _filePath = Path.Combine(environment.ContentRootPath, "App_Data", "designer-projects.json");
    }

    public async Task<IReadOnlyList<ProjectSummaryViewModel>> GetAccessibleProjectsAsync(string email, CancellationToken cancellationToken = default)
    {
        var normalizedEmail = NormalizeEmail(email);
        await _gate.WaitAsync(cancellationToken);
        try
        {
            var projects = await ReadProjectsUnsafeAsync(cancellationToken);
            return projects
                .Where(project => CanAccess(project, normalizedEmail))
                .OrderByDescending(project => project.UpdatedAt)
                .Select(project => ToSummary(project, normalizedEmail))
                .ToList();
        }
        finally
        {
            _gate.Release();
        }
    }

    public async Task<DesignerProject?> GetProjectAsync(string projectId, string email, CancellationToken cancellationToken = default)
    {
        var normalizedEmail = NormalizeEmail(email);
        await _gate.WaitAsync(cancellationToken);
        try
        {
            var projects = await ReadProjectsUnsafeAsync(cancellationToken);
            return projects.FirstOrDefault(project => project.Id == projectId && CanAccess(project, normalizedEmail));
        }
        finally
        {
            _gate.Release();
        }
    }

    public async Task<DesignerProject> CreateProjectAsync(string ownerEmail, string projectName, CancellationToken cancellationToken = default)
    {
        var normalizedOwner = NormalizeEmail(ownerEmail);
        var normalizedName = NormalizeName(projectName);
        await _gate.WaitAsync(cancellationToken);
        try
        {
            var projects = await ReadProjectsUnsafeAsync(cancellationToken);
            var project = new DesignerProject
            {
                Name = normalizedName,
                OwnerEmail = normalizedOwner,
                Schema = new DatabaseSchema { ProjectName = normalizedName },
                CreatedAt = DateTimeOffset.UtcNow,
                UpdatedAt = DateTimeOffset.UtcNow
            };
            projects.Add(project);
            await WriteProjectsUnsafeAsync(projects, cancellationToken);
            return project;
        }
        finally
        {
            _gate.Release();
        }
    }

    public async Task<bool> DeleteProjectAsync(string projectId, string ownerEmail, CancellationToken cancellationToken = default)
    {
        var normalizedOwner = NormalizeEmail(ownerEmail);
        await _gate.WaitAsync(cancellationToken);
        try
        {
            var projects = await ReadProjectsUnsafeAsync(cancellationToken);
            var project = projects.FirstOrDefault(candidate => candidate.Id == projectId);
            if (project is null || !IsOwner(project, normalizedOwner))
            {
                return false;
            }

            projects.Remove(project);
            await WriteProjectsUnsafeAsync(projects, cancellationToken);
            return true;
        }
        finally
        {
            _gate.Release();
        }
    }

    public async Task<DesignerProject?> SaveSchemaAsync(string projectId, string email, DatabaseSchema schema, CancellationToken cancellationToken = default)
    {
        var normalizedEmail = NormalizeEmail(email);
        await _gate.WaitAsync(cancellationToken);
        try
        {
            var projects = await ReadProjectsUnsafeAsync(cancellationToken);
            var project = projects.FirstOrDefault(candidate => candidate.Id == projectId);
            if (project is null || !CanAccess(project, normalizedEmail))
            {
                return null;
            }

            var projectName = NormalizeName(schema.ProjectName);
            schema.ProjectName = projectName;
            schema.Tables ??= new List<SchemaTable>();
            project.Name = projectName;
            project.Schema = schema;
            project.UpdatedAt = DateTimeOffset.UtcNow;
            await WriteProjectsUnsafeAsync(projects, cancellationToken);
            return project;
        }
        finally
        {
            _gate.Release();
        }
    }

    public async Task<DesignerProject?> AddCollaboratorAsync(string projectId, string ownerEmail, string collaboratorEmail, CancellationToken cancellationToken = default)
    {
        var normalizedOwner = NormalizeEmail(ownerEmail);
        var normalizedCollaborator = NormalizeEmail(collaboratorEmail);
        if (!IsValidEmail(normalizedCollaborator))
        {
            return null;
        }

        await _gate.WaitAsync(cancellationToken);
        try
        {
            var projects = await ReadProjectsUnsafeAsync(cancellationToken);
            var project = projects.FirstOrDefault(candidate => candidate.Id == projectId);
            if (project is null || !IsOwner(project, normalizedOwner))
            {
                return null;
            }

            if (!string.Equals(project.OwnerEmail, normalizedCollaborator, StringComparison.OrdinalIgnoreCase) &&
                !project.CollaboratorEmails.Contains(normalizedCollaborator, StringComparer.OrdinalIgnoreCase))
            {
                project.CollaboratorEmails.Add(normalizedCollaborator);
                project.CollaboratorEmails.Sort(StringComparer.OrdinalIgnoreCase);
                project.UpdatedAt = DateTimeOffset.UtcNow;
                await WriteProjectsUnsafeAsync(projects, cancellationToken);
            }

            return project;
        }
        finally
        {
            _gate.Release();
        }
    }

    public async Task<DesignerProject?> RemoveCollaboratorAsync(string projectId, string ownerEmail, string collaboratorEmail, CancellationToken cancellationToken = default)
    {
        var normalizedOwner = NormalizeEmail(ownerEmail);
        var normalizedCollaborator = NormalizeEmail(collaboratorEmail);
        await _gate.WaitAsync(cancellationToken);
        try
        {
            var projects = await ReadProjectsUnsafeAsync(cancellationToken);
            var project = projects.FirstOrDefault(candidate => candidate.Id == projectId);
            if (project is null || !IsOwner(project, normalizedOwner))
            {
                return null;
            }

            project.CollaboratorEmails.RemoveAll(email => string.Equals(email, normalizedCollaborator, StringComparison.OrdinalIgnoreCase));
            project.UpdatedAt = DateTimeOffset.UtcNow;
            await WriteProjectsUnsafeAsync(projects, cancellationToken);
            return project;
        }
        finally
        {
            _gate.Release();
        }
    }

    private async Task<List<DesignerProject>> ReadProjectsUnsafeAsync(CancellationToken cancellationToken)
    {
        if (!File.Exists(_filePath))
        {
            return new List<DesignerProject>();
        }

        await using var stream = File.OpenRead(_filePath);
        return await JsonSerializer.DeserializeAsync<List<DesignerProject>>(stream, _jsonOptions, cancellationToken)
               ?? new List<DesignerProject>();
    }

    private async Task WriteProjectsUnsafeAsync(List<DesignerProject> projects, CancellationToken cancellationToken)
    {
        var directory = Path.GetDirectoryName(_filePath);
        if (!string.IsNullOrWhiteSpace(directory))
        {
            Directory.CreateDirectory(directory);
        }

        await using var stream = File.Create(_filePath);
        await JsonSerializer.SerializeAsync(stream, projects, _jsonOptions, cancellationToken);
    }

    private static ProjectSummaryViewModel ToSummary(DesignerProject project, string viewerEmail)
    {
        return new ProjectSummaryViewModel
        {
            Id = project.Id,
            Name = project.Name,
            OwnerEmail = project.OwnerEmail,
            CollaboratorEmails = project.CollaboratorEmails.ToList(),
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
