namespace NArk.Core.Models.Options;

/// <summary>
/// Configuration for the exit watchtower background service.
/// </summary>
public class ExitWatchtowerOptions
{
    /// <summary>
    /// How often to poll for partial tree broadcasts and progress exits.
    /// </summary>
    public TimeSpan PollInterval { get; set; } = TimeSpan.FromSeconds(60);
}
