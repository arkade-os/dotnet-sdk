using Grpc.Core;
using NArk.Core;

namespace NArk.Transport;

/// <summary>
/// Arkade server (arkd) build version this SDK targets.
/// Sent as the <c>X-Build-Version</c> header on every outgoing request.
/// </summary>
public static class ArkdVersion
{
    public const string TargetBuild = "0.9.9";

    /// <summary>
    /// This SDK's own package version (computed by Nerdbank.GitVersioning from git history),
    /// sent as the <c>X-SDK-VERSION</c> header on every outgoing request. The SemVer build-metadata
    /// suffix (the <c>+commit</c> part of the assembly informational version) is stripped, leaving
    /// the clean version, e.g. <c>1.0.327-beta</c>. Unlike <see cref="TargetBuild"/> (the Arkade
    /// server build this SDK targets), this is the version of the NArk SDK itself.
    /// </summary>
    public static readonly string SdkVersion = StripBuildMetadata(ThisAssembly.AssemblyInformationalVersion);

    /// <summary>
    /// Product token sent as the <c>X-SDK-VERSION</c> header, identifying this SDK and its version
    /// in <c>name/version</c> form, e.g. <c>dotnet-sdk/1.0.327-beta</c>. The name lets arkd
    /// distinguish the .NET SDK from other SDKs (e.g. the TypeScript SDK) on the same wire.
    /// </summary>
    public static readonly string SdkVersionHeaderValue = $"{SdkName}/{SdkVersion}";

    internal const string SdkName = "dotnet-sdk";
    internal const string HeaderName = "X-Build-Version";
    internal const string SdkVersionHeaderName = "X-SDK-VERSION";
    internal const string DigestHeaderName = "X-Digest";

    /// <summary>
    /// Adds the <c>X-Build-Version</c> and <c>X-SDK-VERSION</c> default headers to <paramref name="http"/>.
    /// </summary>
    public static HttpClient InjectHeader(this HttpClient http)
    {
        if (!http.DefaultRequestHeaders.Contains(HeaderName))
        {
            http.DefaultRequestHeaders.TryAddWithoutValidation(HeaderName, TargetBuild);
        }
        if (!http.DefaultRequestHeaders.Contains(SdkVersionHeaderName))
        {
            http.DefaultRequestHeaders.TryAddWithoutValidation(SdkVersionHeaderName, SdkVersionHeaderValue);
        }
        return http;
    }

    /// <summary>
    /// Appends the <c>X-Build-Version</c> and <c>X-SDK-VERSION</c> entries to <paramref name="metadata"/>.
    /// </summary>
    internal static Metadata InjectHeader(this Metadata metadata)
    {
        metadata.Add(HeaderName, TargetBuild);
        metadata.Add(SdkVersionHeaderName, SdkVersionHeaderValue);
        return metadata;
    }

    /// <summary>
    /// Strips the SemVer build-metadata suffix (everything from the first <c>+</c>) from an
    /// assembly informational version, e.g. <c>1.0.327-beta+d238a7c85b</c> → <c>1.0.327-beta</c>.
    /// </summary>
    private static string StripBuildMetadata(string informationalVersion)
    {
        var plus = informationalVersion.IndexOf('+');
        return plus < 0 ? informationalVersion : informationalVersion[..plus];
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
