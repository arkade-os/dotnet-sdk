using System.Text.RegularExpressions;
using Microsoft.EntityFrameworkCore;

namespace NArk.Wallet.Client.Services;

/// <summary>
/// Adds tables the model has and the database does not.
/// </summary>
/// <remarks>
/// <para>
/// This sample keeps its SQLite file in the browser, where it long outlives the build that made it,
/// and creates the schema with <see cref="RelationalDatabaseFacadeExtensions.EnsureCreatedAsync"/>.
/// That call creates everything or nothing: it sees a database, concludes there is nothing to do,
/// and returns — so a table introduced after a wallet was first opened never appears. The failure
/// lands far from the cause, as <c>no such table</c> thrown by whichever query happens to need it.
/// </para>
/// <para>
/// What runs here is EF's own create script, with each statement made idempotent, so the database
/// picks up tables and indexes it is missing while leaving everything already there untouched.
/// </para>
/// <para>
/// <b>It does not add columns to tables that already exist.</b> SQLite would need an <c>ALTER
/// TABLE</c> per column and knowledge of which ones are absent, and guessing at that is how sample
/// code destroys wallets. A released app wants EF migrations; this is the smaller thing that keeps
/// a sample runnable across a schema that is still moving.
/// </para>
/// </remarks>
public static class SchemaBootstrapper
{
    /// <summary>Matches the statements it is safe to re-run once made conditional.</summary>
    private static readonly Regex CreatableStatement = new(
        @"^CREATE\s+(TABLE|UNIQUE\s+INDEX|INDEX)\s+(?!IF\s+NOT\s+EXISTS)",
        RegexOptions.IgnoreCase | RegexOptions.Compiled);

    /// <summary>
    /// Creates any table or index in the model that the database lacks.
    /// </summary>
    /// <param name="db">The context whose model defines the target schema.</param>
    /// <param name="cancellationToken">Cancels between statements.</param>
    public static async Task CreateMissingTablesAsync(
        DbContext db, CancellationToken cancellationToken = default)
    {
        var script = db.Database.GenerateCreateScript();

        foreach (var statement in script.Split(';', StringSplitOptions.RemoveEmptyEntries))
        {
            var trimmed = statement.Trim();
            if (!CreatableStatement.IsMatch(trimmed)) continue;

            // "CREATE TABLE x" -> "CREATE TABLE IF NOT EXISTS x", and the same for either index
            // form. Anything else in the script — pragmas, inserts — is skipped rather than made
            // conditional, because "already applied" is not a thing those can be checked for.
            var conditional = CreatableStatement.Replace(
                trimmed, m => $"CREATE {m.Groups[1].Value} IF NOT EXISTS ");

            await db.Database.ExecuteSqlRawAsync(conditional, cancellationToken);
        }
    }
}
