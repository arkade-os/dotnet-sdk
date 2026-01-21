namespace NArk.Abstractions.Scripts;

public interface IActiveScriptsProvider
{
    event EventHandler? ActiveScriptsChanged;

    Task<HashSet<string>> GetActiveScripts(CancellationToken cancellationToken = default);
}