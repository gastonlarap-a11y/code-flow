using System.Diagnostics;
using LibGit2Sharp;
using Microsoft.Data.Sqlite;

namespace CodeFlow.Diagnostics;

/// <summary>
/// Proves the three native-backed dependencies actually load and work on this machine, before
/// any feature is built on top of them.
/// </summary>
/// <remarks>
/// <para>
/// These are the items <c>docs/business-rules/90-ambiguities.md</c> listed as unresolved: whether LibGit2Sharp's
/// separately-packaged native binaries resolve on <c>osx-arm64</c>, whether Microsoft.Data.Sqlite
/// reads the schema 1.7.2 app produces, and whether Porta.Pty — a single-maintainer
/// package with no alternative, since <c>dotnet/runtime#128565</c> is open against milestone 12 —
/// can spawn a working PTY.
/// </para>
/// <para>
/// This type is temporary. It is deleted once M3 exercises the same ground through real features.
/// It exists so that a failure costs half an hour rather than a rewrite.
/// </para>
/// </remarks>
internal static class SmokeTest
{
    public static async Task<int> RunAsync(CancellationToken cancellationToken)
    {
        var results = new List<(string Name, bool Passed, string Detail)>
        {
            await CheckSqliteAsync(cancellationToken).ConfigureAwait(false),
            CheckLibGit2Sharp(),
            await CheckPtyAsync(cancellationToken).ConfigureAwait(false),
        };

        foreach (var (name, passed, detail) in results)
        {
            Console.WriteLine($"{(passed ? "PASS" : "FAIL")}  {name}");
            Console.WriteLine($"      {detail}");
        }

        var failed = results.Count(r => !r.Passed);
        Console.WriteLine();
        Console.WriteLine(failed == 0
            ? "All dependency smoke checks passed."
            : $"{failed} of {results.Count} dependency smoke checks FAILED.");

        return failed == 0 ? 0 : 1;
    }

    /// <summary>
    /// Creates a database, applies a fragment of the real schema, round-trips a row, and reads
    /// the table list back.
    /// </summary>
    /// <remarks>
    /// Deliberately not pointed at a user's real <c>codeflow.db</c>: no installed copy of 1.7.2
    /// exists on this machine, so claiming "reads an existing install" would be untested. That
    /// claim stays open until a real database is available.
    /// </remarks>
    private static async Task<(string, bool, string)> CheckSqliteAsync(CancellationToken cancellationToken)
    {
        const string name = "Microsoft.Data.Sqlite opens a database and round-trips a row";
        var path = Path.Combine(Path.GetTempPath(), $"codeflow-smoke-{Guid.NewGuid():N}.db");

        try
        {
            await using var connection = new SqliteConnection($"Data Source={path}");
            await connection.OpenAsync(cancellationToken).ConfigureAwait(false);

            // A real fragment of 1.7.2 schema (03-storage.md), including the INTEGER
            // boolean the port has to map explicitly, so this exercises the shape it will meet.
            await ExecuteAsync(connection, """
                CREATE TABLE IF NOT EXISTS workspaces (
                    id          TEXT PRIMARY KEY,
                    name        TEXT NOT NULL,
                    icon        TEXT NOT NULL,
                    color       TEXT NOT NULL,
                    sort_order  INTEGER NOT NULL DEFAULT 0,
                    created_at  TEXT NOT NULL
                );
                """, cancellationToken).ConfigureAwait(false);

            await using (var insert = connection.CreateCommand())
            {
                insert.CommandText =
                    "INSERT INTO workspaces (id, name, icon, color, sort_order, created_at) " +
                    "VALUES ($id, $name, $icon, $color, $order, $created)";
                insert.Parameters.AddWithValue("$id", "ws-smoke");
                insert.Parameters.AddWithValue("$name", "Smoke");
                insert.Parameters.AddWithValue("$icon", "flame");
                insert.Parameters.AddWithValue("$color", "#ff0000");
                insert.Parameters.AddWithValue("$order", 0);
                insert.Parameters.AddWithValue("$created", DateTimeOffset.UtcNow.ToString("O"));
                await insert.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
            }

            await using var read = connection.CreateCommand();
            read.CommandText = "SELECT name FROM workspaces WHERE id = $id";
            read.Parameters.AddWithValue("$id", "ws-smoke");
            var value = (string?)await read.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false);

            var version = connection.ServerVersion;
            return value == "Smoke"
                ? (name, true, $"SQLite {version}, row round-tripped from {Path.GetFileName(path)}")
                : (name, false, $"expected 'Smoke', read '{value ?? "<null>"}'");
        }
        catch (Exception ex)
        {
            return (name, false, $"{ex.GetType().Name}: {ex.Message}");
        }
        finally
        {
            SqliteConnection.ClearAllPools();
            TryDelete(path);
        }
    }

    /// <summary>
    /// Opens this repository through LibGit2Sharp and reads its status.
    /// </summary>
    /// <remarks>
    /// The point is not the status itself — it is that the native binaries, which ship in a
    /// separate <c>LibGit2Sharp.NativeBinaries</c> package and are probed per-RID at runtime,
    /// resolve at all on this architecture. That probing is why the port publishes
    /// self-contained rather than NativeAOT.
    /// </remarks>
    private static (string, bool, string) CheckLibGit2Sharp()
    {
        const string name = "LibGit2Sharp loads its native binaries and reads a repository";

        try
        {
            // Both the assembly's directory and the working directory: a published binary lives
            // outside the source tree, so checking only the former reports a missing repository
            // and hides whether the native library loaded at all — which is the actual question.
            var candidates = new[] { AppContext.BaseDirectory, Directory.GetCurrentDirectory() };
            var repoPath = candidates.Select(Repository.Discover).FirstOrDefault(p => p is not null);
            if (repoPath is null)
            {
                return (name, false,
                    $"no git repository found from {string.Join(" or ", candidates)} — " +
                    "run this from inside a repository to exercise the native library");
            }

            using var repo = new Repository(repoPath);
            var status = repo.RetrieveStatus(new StatusOptions { IncludeUntracked = true });
            var head = repo.Head.FriendlyName;
            var version = GlobalSettings.Version.ToString();

            return (name, true,
                $"libgit2 {version}, HEAD '{head}', {status.Count()} entries in status");
        }
        catch (Exception ex)
        {
            return (name, false, $"{ex.GetType().Name}: {ex.Message}");
        }
    }

    /// <summary>
    /// Spawns a real PTY, writes a command into it and reads the echoed output back.
    /// </summary>
    /// <remarks>
    /// The riskiest of the three. .NET has no first-party PTY and Porta.Pty is a small
    /// single-maintainer package, so this check answers whether the terminal slice has a
    /// foundation at all. A failure here is a finding, not a blocker to work around quietly.
    /// </remarks>
    private static async Task<(string, bool, string)> CheckPtyAsync(CancellationToken cancellationToken)
    {
        const string name = "Porta.Pty spawns a shell and echoes back through the PTY";

        try
        {
            var (ok, detail) = await PtyProbe.RunAsync(cancellationToken).ConfigureAwait(false);
            return (name, ok, detail);
        }
        catch (Exception ex)
        {
            return (name, false, $"{ex.GetType().Name}: {ex.Message}");
        }
    }

    private static async Task ExecuteAsync(SqliteConnection connection, string sql, CancellationToken ct)
    {
        await using var command = connection.CreateCommand();
        command.CommandText = sql;
        await command.ExecuteNonQueryAsync(ct).ConfigureAwait(false);
    }

    private static void TryDelete(string path)
    {
        try
        {
            if (File.Exists(path))
            {
                File.Delete(path);
            }
        }
        catch (IOException)
        {
            // A leftover temp file is not worth failing a diagnostic over.
        }
    }
}
