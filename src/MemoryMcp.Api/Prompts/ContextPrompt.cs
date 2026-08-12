using System.ComponentModel;
using System.Text;
using MemoryMcp.Application.Abstractions;
using MemoryMcp.Application.Memories;
using ModelContextProtocol.Server;

namespace MemoryMcp.Api.Prompts;

[McpServerPromptType]
public sealed class ContextPrompt(IMemoryService memoryService, ICurrentAccessContext accessContext)
{
    private const int RecentSpacesTake = 3;

    [McpServerPrompt(Name = "context")]
    [Description("A ready-to-attach context message: profile for the active space, plus up to three other recently active spaces.")]
    public Task<string> Context(CancellationToken cancellationToken = default) =>
        McpExecution.RunAsync(async () =>
        {
            var activeGrant = accessContext.ActiveGrant ?? throw new SpaceNotFoundException(null);
            var profile = await memoryService.GetProfileAsync(activeGrant.SpaceKey, cancellationToken);
            var recentSpaces = await RankByRecentActivityAsync(
                accessContext.Grants.Where(g => g.SpaceId != activeGrant.SpaceId).ToList(), cancellationToken);

            return BuildMessage(activeGrant, profile, recentSpaces);
        });

    // No "last used" tracking exists on ApiKeySpaceGrant, so recency is derived from each space's
    // most recently created memory (already ordered newest-first by ListMemoriesAsync); spaces with
    // no memories yet sort last rather than being excluded.
    private async Task<List<(SpaceGrant Grant, DateTimeOffset? LastActivity)>> RankByRecentActivityAsync(
        IReadOnlyList<SpaceGrant> grants, CancellationToken cancellationToken)
    {
        var withActivity = new List<(SpaceGrant Grant, DateTimeOffset? LastActivity)>();
        foreach (var grant in grants)
        {
            var page = await memoryService.ListMemoriesAsync(grant.SpaceKey, page: 1, limit: 1, cancellationToken);
            withActivity.Add((grant, page.Items.Count > 0 ? page.Items[0].CreatedAt : null));
        }

        return withActivity
            .OrderByDescending(x => x.LastActivity ?? DateTimeOffset.MinValue)
            .Take(RecentSpacesTake)
            .ToList();
    }

    private static string BuildMessage(
        SpaceGrant active, IReadOnlyList<MemorySummaryDto> profile, IReadOnlyList<(SpaceGrant Grant, DateTimeOffset? LastActivity)> recentSpaces)
    {
        var message = new StringBuilder();
        message.AppendLine($"Active space: \"{active.SpaceName}\" ({active.SpaceKey})");
        message.AppendLine();

        if (profile.Count > 0)
        {
            message.AppendLine("Recent memories:");
            foreach (var memory in profile)
            {
                message.AppendLine($"- {memory.Text}");
            }
        }
        else
        {
            message.AppendLine("No memories saved yet in this space.");
        }

        if (recentSpaces.Count > 0)
        {
            message.AppendLine();
            message.AppendLine("Other recently active spaces:");
            foreach (var (grant, lastActivity) in recentSpaces)
            {
                var recency = lastActivity is { } t ? $"last activity {t:yyyy-MM-dd}" : "no activity yet";
                message.AppendLine($"- \"{grant.SpaceName}\" ({grant.SpaceKey}) — {recency}");
            }
        }

        return message.ToString().TrimEnd();
    }
}
