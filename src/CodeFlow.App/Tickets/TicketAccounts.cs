using System.Text.Json;
using CodeFlow.Storage;
using CodeFlow.Workspaces;
using Microsoft.Data.Sqlite;

namespace CodeFlow.Tickets;

/// <summary>
/// Decides which Azure DevOps account a project's tickets come from.
/// </summary>
/// <remarks>
/// <para>
/// Exists because one install legitimately holds several organisations — a work one and a personal
/// one — and because <b>the board is not necessarily where the code is</b>. Inferring the
/// organisation from the repository's own link would be right most of the time and silently wrong
/// exactly when someone has both.
/// </para>
/// <para>
/// <b>Its most important answer is "I don't know".</b> When nothing decides it, this reports
/// <c>none</c> rather than picking the first connection, so the UI can ask. The alternative is an
/// app that quietly reads the wrong organisation's board and shows an empty list.
/// </para>
/// </remarks>
internal static class TicketAccounts
{
    /// <summary>The app setting the renderer keeps its Azure connections in.</summary>
    /// <remarks>
    /// Written only by the renderer (<c>renderer/src/lib/adoConnections.ts</c>) and holding
    /// organisation names alone — never a PAT, which lives in the OS keychain.
    /// </remarks>
    private const string ConnectionsSetting = "ado_connections";

    public const string FromWorkspace = "workspace";

    public const string FromProject = "project";

    public const string FromOnlyConnection = "only_connection";

    public const string Undecided = "none";

    /// <summary>Which organisation and board project a project's tickets come from.</summary>
    public static TicketAccount Resolve(SqliteConnection connection, string projectId)
    {
        // Sql.QuerySingle is constrained to reference types, so the row comes back as a one-element
        // list — the same shape WorkspaceStore.ResolveGitIdentity uses for its own tuple read.
        var rows = Sql.Query(connection,
            """
            SELECT p.ado_org, p.ado_project, w.ado_org, w.ado_project
            FROM projects p JOIN workspaces w ON w.id = p.workspace_id
            WHERE p.id = $projectId
            """,
            reader => (
                ProjectOrg: reader.TextOrNull(0),
                ProjectBoard: reader.TextOrNull(1),
                WorkspaceOrg: reader.TextOrNull(2),
                WorkspaceBoard: reader.TextOrNull(3)),
            ("$projectId", projectId));

        // A project id that names nothing is a caller error, not a missing account: answering
        // "undecided" would send the user to a picker that cannot help them.
        if (rows.Count == 0)
        {
            throw new ArgumentException($"no project '{projectId}'");
        }

        var (projectOrg, projectBoard, workspaceOrg, workspaceBoard) = rows[0];

        // The board project follows the same "explicit choice wins" order as the organisation, and
        // it needs one: a repository hosted on GitHub has no `projects.ado_project` at all, so
        // reading only that column left the workspace organisation set and the board unaddressable —
        // the picker then failed with the very message the user had just acted on.
        var board = NonBlank(workspaceBoard) ?? NonBlank(projectBoard);

        if (NonBlank(workspaceOrg) is { } chosen)
        {
            return new TicketAccount(chosen, board, FromWorkspace);
        }

        if (NonBlank(projectOrg) is { } linked)
        {
            return new TicketAccount(linked, board, FromProject);
        }

        // One connection is not a choice anybody made, but it is the only one that could be meant.
        var connections = Connections(connection);
        return connections.Count == 1
            ? new TicketAccount(connections[0], board, FromOnlyConnection)
            : new TicketAccount(null, board, Undecided);
    }

    /// <summary>
    /// The organisations the user has connected.
    /// </summary>
    /// <remarks>
    /// The setting is free-form JSON the renderer owns, so a shape this does not recognise is read
    /// as "no connections" rather than throwing: a malformed setting must not make the tickets
    /// module unusable, and the settings screen is where it gets fixed.
    /// </remarks>
    public static List<string> Connections(SqliteConnection connection)
    {
        var stored = Settings.GetSetting(connection, ConnectionsSetting);
        if (string.IsNullOrWhiteSpace(stored))
        {
            return [];
        }

        try
        {
            using var document = JsonDocument.Parse(stored);
            if (document.RootElement.ValueKind != JsonValueKind.Array)
            {
                return [];
            }

            return [.. document.RootElement
                .EnumerateArray()
                .Select(element => element.TryGetProperty("org", out var org) ? org.GetString() : null)
                .Select(NonBlank)
                .OfType<string>()];
        }
        catch (JsonException)
        {
            return [];
        }
    }

    private static string? NonBlank(string? value) => string.IsNullOrWhiteSpace(value) ? null : value.Trim();
}
