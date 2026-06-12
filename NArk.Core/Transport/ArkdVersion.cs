using Grpc.Core;

namespace NArk.Transport;

/// <summary>
/// Arkade server (arkd) build version this SDK targets.
/// Sent as the <c>X-Build-Version</c> header on every outgoing request.
/// </summary>
public static class ArkdVersion
{
    public const string TargetBuild = "0.9.7";
    internal const string HeaderName = "X-Build-Version";

    /// <summary>
    /// Adds the <c>X-Build-Version</c> default header to <paramref name="http"/>.
    /// </summary>
    public static HttpClient InjectHeader(this HttpClient http)
    {
        http.DefaultRequestHeaders.TryAddWithoutValidation(HeaderName, TargetBuild);
        return http;
    }

    /// <summary>
    /// Appends the <c>X-Build-Version</c> entry to <paramref name="metadata"/>.
    /// </summary>
    internal static Metadata InjectHeader(this Metadata metadata)
    {
        metadata.Add(HeaderName, TargetBuild);
        return metadata;
    }
}
