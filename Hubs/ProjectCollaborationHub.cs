using System.Collections.Concurrent;
using System.Security.Claims;
using DoAnLapTrinhWeb.Models.Designer;
using DoAnLapTrinhWeb.Services;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.SignalR;

namespace DoAnLapTrinhWeb.Hubs;

[Authorize]
public class ProjectCollaborationHub : Hub
{
    private static readonly ConcurrentDictionary<string, HashSet<string>> JoinedProjects = new();
    private static readonly ConcurrentDictionary<string, ConcurrentDictionary<string, MemberPresence>> ProjectMembers = new();
    private readonly ProjectStore _projectStore;

    public ProjectCollaborationHub(ProjectStore projectStore)
    {
        _projectStore = projectStore;
    }

    public async Task JoinProject(string projectId, string? color = null)
    {
        if (string.IsNullOrWhiteSpace(projectId)) return;
        var cleanId = projectId.Trim().ToLowerInvariant();

        if (!await CanAccessProjectAsync(cleanId))
        {
            throw new HubException("You do not have access to this project.");
        }

        var projects = JoinedProjects.GetOrAdd(Context.ConnectionId, _ => new HashSet<string>(StringComparer.OrdinalIgnoreCase));
        lock (projects)
        {
            projects.Add(cleanId);
        }

        await Groups.AddToGroupAsync(Context.ConnectionId, GroupName(cleanId));
        var members = ProjectMembers.GetOrAdd(cleanId, _ => new ConcurrentDictionary<string, MemberPresence>());
        members[Context.ConnectionId] = new MemberPresence(CurrentEmail(), NormalizeColor(color));

        await Clients.Group(GroupName(cleanId)).SendAsync("PresenceUpdated", GetProjectPresence(cleanId));
        await Clients.Group(GroupName(cleanId)).SendAsync("ActivityUpdated", CreateActivity("joined the project"));
    }

    public async Task BroadcastSchema(string projectId, DatabaseSchema schema, string? activity = null)
    {
        if (string.IsNullOrWhiteSpace(projectId)) return;
        var cleanId = projectId.Trim().ToLowerInvariant();

        if (!HasJoined(cleanId))
        {
            throw new HubException("Join the project before sending updates.");
        }

        var project = await _projectStore.SaveSchemaAsync(cleanId, CurrentEmail(), schema);
        if (project is null)
        {
            throw new HubException("Unable to save project update.");
        }

        await Clients.OthersInGroup(GroupName(cleanId)).SendAsync("SchemaUpdated", schema, CurrentEmail());
        await Clients.Group(GroupName(cleanId)).SendAsync("ActivityUpdated", CreateActivity(activity ?? "updated the database schema"));
    }

    public async Task MoveCursor(string projectId, double x, double y, string color)
    {
        if (string.IsNullOrWhiteSpace(projectId)) return;
        var cleanId = projectId.Trim().ToLowerInvariant();

        if (!HasJoined(cleanId))
        {
            return;
        }

        await Clients.OthersInGroup(GroupName(cleanId)).SendAsync("CursorMoved", new
        {
            email = CurrentEmail(),
            x,
            y,
            color
        });
    }

    public async Task LeaveProject(string projectId)
    {
        if (string.IsNullOrWhiteSpace(projectId)) return;
        var cleanId = projectId.Trim().ToLowerInvariant();

        if (JoinedProjects.TryGetValue(Context.ConnectionId, out var projects))
        {
            lock (projects)
            {
                projects.Remove(cleanId);
            }
        }

        await Groups.RemoveFromGroupAsync(Context.ConnectionId, GroupName(cleanId));
        RemoveMember(cleanId);
        await Clients.Group(GroupName(cleanId)).SendAsync("PresenceUpdated", GetProjectPresence(cleanId));
        await Clients.OthersInGroup(GroupName(cleanId)).SendAsync("ActivityUpdated", CreateActivity("left the project"));
    }

    public override async Task OnDisconnectedAsync(Exception? exception)
    {
        if (JoinedProjects.TryRemove(Context.ConnectionId, out var projects))
        {
            string[] projectIds;
            lock (projects)
            {
                projectIds = projects.ToArray();
            }

            foreach (var projectId in projectIds)
            {
                var cleanId = projectId.Trim().ToLowerInvariant();
                RemoveMember(cleanId);
                await Clients.Group(GroupName(cleanId)).SendAsync("PresenceUpdated", GetProjectPresence(cleanId));
                await Clients.OthersInGroup(GroupName(cleanId)).SendAsync("ActivityUpdated", CreateActivity("left the project"));
            }
        }

        await base.OnDisconnectedAsync(exception);
    }

    private async Task<bool> CanAccessProjectAsync(string projectId)
    {
        if (string.IsNullOrWhiteSpace(projectId))
        {
            return false;
        }
        var cleanId = projectId.Trim().ToLowerInvariant();
        return await _projectStore.GetProjectAsync(cleanId, CurrentEmail()) is not null;
    }

    private bool HasJoined(string projectId)
    {
        if (string.IsNullOrWhiteSpace(projectId)) return false;
        var cleanId = projectId.Trim().ToLowerInvariant();

        if (!JoinedProjects.TryGetValue(Context.ConnectionId, out var projects))
        {
            return false;
        }

        lock (projects)
        {
            return projects.Contains(cleanId);
        }
    }

    private string CurrentEmail()
    {
        return Context.User?.FindFirstValue(ClaimTypes.Email) ??
               Context.User?.FindFirstValue("email") ??
               string.Empty;
    }

    private static string NormalizeColor(string? color)
    {
        if (string.IsNullOrWhiteSpace(color))
        {
            return "#1485f5";
        }

        return color.StartsWith('#') && color.Length <= 16 ? color : "#1485f5";
    }

    private void RemoveMember(string projectId)
    {
        var cleanId = projectId.Trim().ToLowerInvariant();
        if (!ProjectMembers.TryGetValue(cleanId, out var members))
        {
            return;
        }

        members.TryRemove(Context.ConnectionId, out _);
        if (members.IsEmpty)
        {
            ProjectMembers.TryRemove(cleanId, out _);
        }
    }

    private static object[] GetProjectPresence(string projectId)
    {
        var cleanId = projectId.Trim().ToLowerInvariant();
        if (!ProjectMembers.TryGetValue(cleanId, out var members))
        {
            return [];
        }

        return members.Values
            .GroupBy(member => member.Email, StringComparer.OrdinalIgnoreCase)
            .Select(group => new
            {
                email = group.Key,
                color = group.First().Color,
                sessions = group.Count()
            })
            .OrderBy(member => member.email, StringComparer.OrdinalIgnoreCase)
            .Cast<object>()
            .ToArray();
    }

    private object CreateActivity(string action)
    {
        return new
        {
            email = CurrentEmail(),
            action,
            at = DateTimeOffset.UtcNow
        };
    }

    private static string GroupName(string projectId)
    {
        return $"project:{projectId?.Trim().ToLowerInvariant()}";
    }

    private sealed record MemberPresence(string Email, string Color);
}
