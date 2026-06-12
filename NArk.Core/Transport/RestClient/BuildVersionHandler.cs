using System.Text;
using NArk.Core;
using NArk.Core.Transport;

namespace NArk.Transport.RestClient;

/// <summary>
/// HTTP message handler that appends <c>X-Build-Version</c> and <c>X-Digest</c> headers to every outgoing request.
/// <list type="bullet">
///   <item>If the server responds with <c>BUILD_VERSION_TOO_OLD</c>, throws <see cref="IncompatibleSdkVersionException"/>.</item>
///   <item>If the server responds with <c>DIGEST_MISMATCH</c>, clears the cached digest and throws <see cref="DigestMismatchException"/>.</item>
/// </list>
/// Both exceptions propagate to the caller; the SDK does not catch them.
/// </summary>
internal sealed class BuildVersionHandler(DigestHolder digestHolder) : DelegatingHandler
{
    protected override async Task<HttpResponseMessage> SendAsync(
        HttpRequestMessage request,
        CancellationToken cancellationToken)
    {
        var digest = digestHolder.Digest;
        if (digest is not null)
            request.Headers.TryAddWithoutValidation(ArkdVersion.DigestHeaderName, digest);

        var response = await base.SendAsync(request, cancellationToken).ConfigureAwait(false);

        if (!response.IsSuccessStatusCode)
        {
            var body = await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);

            ArkdVersion.ThrowIfVersionRejected(body);

            if (body.Contains("DIGEST_MISMATCH", StringComparison.OrdinalIgnoreCase))
            {
                digestHolder.Clear();
                throw new DigestMismatchException(
                    "Arkade server reported a configuration digest mismatch. Server info cache has been cleared; retry after calling GetServerInfoAsync.");
            }

            // Re-wrap so callers can still read the body after we consumed it.
            response.Content = new StringContent(body, Encoding.UTF8, "application/json");
        }

        return response;
    }
}
