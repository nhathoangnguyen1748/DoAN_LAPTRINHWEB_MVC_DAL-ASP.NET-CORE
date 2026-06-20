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
        if (!await CanAccessProjectAsync(projectId))
        {
            throw new HubException("You do not have access to this project.");
        }

        var projects = JoinedProjects.GetOrAdd(Context.ConnectionId, _ => new HashSet<string>(StringComparer.Ordinal));
        lock (projects)
        {
            projects.Add(projectId);
        }

        await Groups.AddToGroupAsync(Context.ConnectionId, GroupName(projectId));
        var members = ProjectMembers.GetOrAdd(projectId, _ => new ConcurrentDictionary<string, MemberPresence>());
        members[Context.ConnectionId] = new MemberPresence(CurrentEmail(), NormalizeColor(color));

        await Clients.Group(GroupName(projectId)).SendAsync("PresenceUpdated", GetProjectPresence(projectId));
        await Clients.Group(GroupName(projectId)).SendAsync("ActivityUpdated", CreateActivity("joined the project"));
    }

    public async Task BroadcastSchema(string projectId, DatabaseSchema schema, string? activity = null)
    {
        if (!HasJoined(projectId))
        {
            throw new HubException("Join the project before sending updates.");
        }

        var project = await _projectStore.SaveSchemaAsync(projectId, CurrentEmail(), schema);
        if (project is null)
        {
            throw new HubException("Unable to save project update.");
        }

        await Clients.OthersInGroup(GroupName(projectId)).SendAsync("SchemaUpdated", schema, CurrentEmail());
        await Clients.Group(GroupName(projectId)).SendAsync("ActivityUpdated", CreateActivity(activity ?? "updated the database schema"));
    }

    public async Task MoveCursor(string projectId, double x, double y, string color)
    {
        if (!HasJoined(projectId))
        {
            return;
        }

        await Clients.OthersInGroup(GroupName(projectId)).SendAsync("CursorMoved", new
        {
            email = CurrentEmail(),
            x,
            y,
            color
        });
    }

    public async Task LeaveProject(string projectId)
    {
        if (JoinedProjects.TryGetValue(Context.ConnectionId, out var projects))
        {
            lock (projects)
            {
                projects.Remove(projectId);
            }
        }

        await Groups.RemoveFromGroupAsync(Context.ConnectionId, GroupName(projectId));
        RemoveMember(projectId);
        await Clients.Group(GroupName(projectId)).SendAsync("PresenceUpdated", GetProjectPresence(projectId));
        await Clients.OthersInGroup(GroupName(projectId)).SendAsync("ActivityUpdated", CreateActivity("left the project"));
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
                RemoveMember(projectId);
                await Clients.Group(GroupName(projectId)).SendAsync("PresenceUpdated", GetProjectPresence(projectId));
                await Clients.OthersInGroup(GroupName(projectId)).SendAsync("ActivityUpdated", CreateActivity("left the project"));
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

        return await _projectStore.GetProjectAsync(projectId, CurrentEmail()) is not null;
    }

    private bool HasJoined(string projectId)
    {
        if (!JoinedProjects.TryGetValue(Context.ConnectionId, out var projects))
        {
            return false;
        }

        lock (projects)
        {
            return projects.Contains(projectId);
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
        if (!ProjectMembers.TryGetValue(projectId, out var members))
        {
            return;
        }

        members.TryRemove(Context.ConnectionId, out _);
        if (members.IsEmpty)
        {
            ProjectMembers.TryRemove(projectId, out _);
        }
    }

    private static object[] GetProjectPresence(string projectId)
    {
        if (!ProjectMembers.TryGetValue(projectId, out var members))
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
        return $"project:{projectId}";
    }

    private sealed record MemberPresence(string Email, string Color);
}
