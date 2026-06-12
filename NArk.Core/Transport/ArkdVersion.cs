using Grpc.Core;
using NArk.Core;

namespace NArk.Transport;

/// <summary>
/// Arkade server (arkd) build version this SDK targets.
/// Sent as the <c>X-Build-Version</c> header on every outgoing request.
/// </summary>
public static class ArkdVersion
{
    public const string TargetBuild = "0.9.7";
    internal const string HeaderName = "X-Build-Version";
    internal const string DigestHeaderName = "X-Digest";

    /// <summary>
    /// Adds the <c>X-Build-Version</c> default header to <paramref name="http"/>.
    /// </summary>
    public static HttpClient InjectHeader(this HttpClient http)
    {
        if (!http.DefaultRequestHeaders.Contains(HeaderName))
        {
            http.DefaultRequestHeaders.TryAddWithoutValidation(HeaderName, TargetBuild);
        }
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

    /// <summary>
    /// Throws <see cref="IncompatibleSdkVersionException"/> when <paramref name="errorDetail"/> contains
    /// <c>BUILD_VERSION_TOO_OLD</c>. The exception propagates to the caller; the SDK does not catch it.
    /// </summary>
    /// <exception cref="IncompatibleSdkVersionException">
    /// Thrown when the Arkade server rejects the current SDK build version.
    /// </exception>
    internal static void ThrowIfVersionRejected(string errorDetail)
    {
        if (!errorDetail.Contains("BUILD_VERSION_TOO_OLD", StringComparison.OrdinalIgnoreCase))
            return;
        throw new IncompatibleSdkVersionException(
            $"Arkade server rejected SDK build {TargetBuild}: server requires a newer SDK version. Upgrade the NArk SDK package.");
    }
}
