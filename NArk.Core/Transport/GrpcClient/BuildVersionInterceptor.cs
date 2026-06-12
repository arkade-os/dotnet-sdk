using Grpc.Core;
using Grpc.Core.Interceptors;
using NArk.Core;

namespace NArk.Transport.GrpcClient;

/// <summary>
/// gRPC interceptor that appends the <c>X-Build-Version</c> header to every outgoing call,
/// advertising the target Arkade server build this SDK was compiled against.
/// If the server responds with <c>BUILD_VERSION_TOO_OLD</c>, an <see cref="NArk.Core.IncompatibleSdkVersionException"/>
/// is thrown and propagates to the caller; the SDK does not catch it.
/// </summary>
internal sealed class BuildVersionInterceptor : Interceptor
{
    public override AsyncUnaryCall<TResponse> AsyncUnaryCall<TRequest, TResponse>(
        TRequest request,
        ClientInterceptorContext<TRequest, TResponse> context,
        AsyncUnaryCallContinuation<TRequest, TResponse> continuation)
    {
        var call = continuation(request, WithBuildVersion(context));
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
        var call = continuation(request, WithBuildVersion(context));
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
        var call = continuation(WithBuildVersion(context));
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
        var call = continuation(WithBuildVersion(context));
        return new AsyncDuplexStreamingCall<TRequest, TResponse>(
            call.RequestStream,
            call.ResponseStream,
            GuardAsync(call.ResponseHeadersAsync),
            call.GetStatus,
            call.GetTrailers,
            () => call.Dispose());
    }

    private static async Task<T> GuardAsync<T>(Task<T> task)
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
    }

    private static ClientInterceptorContext<TRequest, TResponse> WithBuildVersion<TRequest, TResponse>(
        ClientInterceptorContext<TRequest, TResponse> context)
        where TRequest : class
        where TResponse : class
    {
        var headers = new Metadata();
        foreach (var entry in context.Options.Headers ?? Enumerable.Empty<Metadata.Entry>())
            headers.Add(entry);

        headers.InjectHeader();

        var options = context.Options.WithHeaders(headers);
        return new ClientInterceptorContext<TRequest, TResponse>(context.Method, context.Host, options);
    }
}
