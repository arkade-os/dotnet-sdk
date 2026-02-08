using Grpc.Core;
using Grpc.Core.Interceptors;

namespace NArk.Transport.GrpcClient;

/// <summary>
/// gRPC interceptor that adds a deadline to all outgoing calls if one isn't already set.
/// </summary>
public class DeadlineInterceptor(TimeSpan defaultDeadline) : Interceptor
{
    public override AsyncUnaryCall<TResponse> AsyncUnaryCall<TRequest, TResponse>(
        TRequest request,
        ClientInterceptorContext<TRequest, TResponse> context,
        AsyncUnaryCallContinuation<TRequest, TResponse> continuation)
    {
        return continuation(request, ApplyDeadline(context));
    }

    public override AsyncServerStreamingCall<TResponse> AsyncServerStreamingCall<TRequest, TResponse>(
        TRequest request,
        ClientInterceptorContext<TRequest, TResponse> context,
        AsyncServerStreamingCallContinuation<TRequest, TResponse> continuation)
    {
        return continuation(request, ApplyDeadline(context));
    }

    public override AsyncClientStreamingCall<TRequest, TResponse> AsyncClientStreamingCall<TRequest, TResponse>(
        ClientInterceptorContext<TRequest, TResponse> context,
        AsyncClientStreamingCallContinuation<TRequest, TResponse> continuation)
    {
        return continuation(ApplyDeadline(context));
    }

    public override AsyncDuplexStreamingCall<TRequest, TResponse> AsyncDuplexStreamingCall<TRequest, TResponse>(
        ClientInterceptorContext<TRequest, TResponse> context,
        AsyncDuplexStreamingCallContinuation<TRequest, TResponse> continuation)
    {
        return continuation(ApplyDeadline(context));
    }

    private ClientInterceptorContext<TRequest, TResponse> ApplyDeadline<TRequest, TResponse>(
        ClientInterceptorContext<TRequest, TResponse> context)
        where TRequest : class
        where TResponse : class
    {
        // Only apply deadline if one isn't already set
        if (context.Options.Deadline is null)
        {
            var options = context.Options.WithDeadline(DateTime.UtcNow + defaultDeadline);
            return new ClientInterceptorContext<TRequest, TResponse>(context.Method, context.Host, options);
        }

        return context;
    }
}
