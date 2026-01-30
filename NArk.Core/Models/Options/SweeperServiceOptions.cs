namespace NArk.Core.Models.Options;

public class SweeperServiceOptions
{
    public TimeSpan ForceRefreshInterval { get; set; } = TimeSpan.Zero;
}