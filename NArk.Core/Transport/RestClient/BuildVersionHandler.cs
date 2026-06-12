using System.Net.Http.Headers;
using System.Text;

namespace NArk.Transport.RestClient;

/// <summary>
/// HTTP message handler that inspects error responses for <c>BUILD_VERSION_TOO_OLD</c>.
/// When detected, throws <see cref="NArk.Core.IncompatibleSdkVersionException"/> which propagates to the caller;
/// the SDK does not catch it.
/// </summary>
internal sealed class BuildVersionHandler : DelegatingHandler
{
    protected override async Task<HttpResponseMessage> SendAsync(
        HttpRequestMessage request,
        CancellationToken cancellationToken)
    {
        var response = await base.SendAsync(request, cancellationToken).ConfigureAwait(false);

        if (!response.IsSuccessStatusCode)
        {
            var body = await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
            ArkdVersion.ThrowIfVersionRejected(body);
            // Re-wrap so callers can still read the body after we consumed it.
            response.Content = new StringContent(body, Encoding.UTF8, "application/json");
        }

        return response;
    }
}
