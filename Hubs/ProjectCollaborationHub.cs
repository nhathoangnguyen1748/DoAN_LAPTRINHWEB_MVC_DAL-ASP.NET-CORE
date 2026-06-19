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
    private readonly ProjectStore _projectStore;

    public ProjectCollaborationHub(ProjectStore projectStore)
    {
        _projectStore = projectStore;
    }

    public async Task JoinProject(string projectId)
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
        await Clients.OthersInGroup(GroupName(projectId)).SendAsync("UserJoined", CurrentEmail());
    }

    public async Task BroadcastSchema(string projectId, DatabaseSchema schema)
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
        await Clients.OthersInGroup(GroupName(projectId)).SendAsync("UserLeft", CurrentEmail());
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
                await Clients.OthersInGroup(GroupName(projectId)).SendAsync("UserLeft", CurrentEmail());
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

    private static string GroupName(string projectId)
    {
        return $"project:{projectId}";
    }
}
