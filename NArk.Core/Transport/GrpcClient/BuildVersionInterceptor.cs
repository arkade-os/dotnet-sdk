using Grpc.Core;
using Grpc.Core.Interceptors;
using NArk.Core;
using NArk.Core.Transport;

namespace NArk.Transport.GrpcClient;

/// <summary>
/// gRPC interceptor that appends <c>X-Build-Version</c> and <c>X-Digest</c> headers to every outgoing call.
/// <list type="bullet">
///   <item>If the server responds with <c>BUILD_VERSION_TOO_OLD</c>, throws <see cref="IncompatibleSdkVersionException"/>.</item>
///   <item>If the server responds with <c>DIGEST_MISMATCH</c>, clears the cached digest and throws <see cref="DigestMismatchException"/>.</item>
/// </list>
/// Both exceptions propagate to the caller; the SDK does not catch them.
/// </summary>
internal sealed class BuildVersionInterceptor(DigestHolder digestHolder) : Interceptor
{
    public override AsyncUnaryCall<TResponse> AsyncUnaryCall<TRequest, TResponse>(
        TRequest request,
        ClientInterceptorContext<TRequest, TResponse> context,
        AsyncUnaryCallContinuation<TRequest, TResponse> continuation)
    {
        var call = continuation(request, WithHeaders(context));
        return new AsyncUnaryCall<TResponse>(
            GuardAsync(call.ResponseAsync),
            call.ResponseHeadersAsync,
            call.GetStatus,
            call.GetTrailers,
            () => call.Dispose());
    }

    public override AsyncServerStreamingCall<TResponse> AsyncServerStreamingCall<TRequest, TResponse>(
        TRequest request,
        ClientInterceptorContext<TRequest, TResponse> context,
        AsyncServerStreamingCallContinuation<TRequest, TResponse> continuation)
    {
        var call = continuation(request, WithHeaders(context));
        return new AsyncServerStreamingCall<TResponse>(
            call.ResponseStream,
            GuardAsync(call.ResponseHeadersAsync),
            call.GetStatus,
            call.GetTrailers,
            () => call.Dispose());
    }

    public override AsyncClientStreamingCall<TRequest, TResponse> AsyncClientStreamingCall<TRequest, TResponse>(
        ClientInterceptorContext<TRequest, TResponse> context,
        AsyncClientStreamingCallContinuation<TRequest, TResponse> continuation)
    {
        var call = continuation(WithHeaders(context));
        return new AsyncClientStreamingCall<TRequest, TResponse>(
            call.RequestStream,
            GuardAsync(call.ResponseAsync),
            call.ResponseHeadersAsync,
            call.GetStatus,
            call.GetTrailers,
            () => call.Dispose());
    }

    public override AsyncDuplexStreamingCall<TRequest, TResponse> AsyncDuplexStreamingCall<TRequest, TResponse>(
        ClientInterceptorContext<TRequest, TResponse> context,
        AsyncDuplexStreamingCallContinuation<TRequest, TResponse> continuation)
    {
        var call = continuation(WithHeaders(context));
        return new AsyncDuplexStreamingCall<TRequest, TResponse>(
            call.RequestStream,
            call.ResponseStream,
            GuardAsync(call.ResponseHeadersAsync),
            call.GetStatus,
            call.GetTrailers,
            () => call.Dispose());
    }

    private async Task<T> GuardAsync<T>(Task<T> task)
    {
        try
        {
            return await task.ConfigureAwait(false);
        }
        catch (RpcException ex) when (ex.Status.Detail.Contains("BUILD_VERSION_TOO_OLD", StringComparison.OrdinalIgnoreCase))
        {
            throw new IncompatibleSdkVersionException(
                $"Arkade server rejected SDK build {ArkdVersion.TargetBuild}: server requires a newer SDK version. Upgrade the NArk SDK package.");
        }
        catch (RpcException ex) when (ex.Status.Detail.Contains("DIGEST_MISMATCH", StringComparison.OrdinalIgnoreCase))
        {
            digestHolder.Clear();
            throw new DigestMismatchException(
                "Arkade server reported a configuration digest mismatch. Server info cache has been cleared; retry after calling GetServerInfoAsync.");
        }
    }

    private ClientInterceptorContext<TRequest, TResponse> WithHeaders<TRequest, TResponse>(
        ClientInterceptorContext<TRequest, TResponse> context)
        where TRequest : class
        where TResponse : class
    {
        var headers = new Metadata();
        foreach (var entry in context.Options.Headers ?? Enumerable.Empty<Metadata.Entry>())
            headers.Add(entry);

        headers.InjectHeader();

        var digest = digestHolder.Digest;
        if (digest is not null)
            headers.Add(ArkdVersion.DigestHeaderName, digest);

        var options = context.Options.WithHeaders(headers);
        return new ClientInterceptorContext<TRequest, TResponse>(context.Method, context.Host, options);
    }
}
