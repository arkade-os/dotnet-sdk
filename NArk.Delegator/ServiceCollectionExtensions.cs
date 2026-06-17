using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.DependencyInjection;
using NArk.Core.Models.Options;
using NArk.Core.Services;

namespace NArk.Delegator;

/// <summary>DI + endpoint helpers for hosting the Arkade delegator service in any ASP.NET Core app.</summary>
public static class ServiceCollectionExtensions
{
    /// <summary>
    /// Registers the delegator service handler and options, plus gRPC with JSON transcoding so the
    /// REST endpoints (from the proto's google.api.http annotations) are exposed alongside gRPC. Call
    /// <see cref="MapNArkDelegator"/> on the app to map the endpoints. The host must also register the
    /// NArk wallet, transport, and intent storage, and (for refresh execution) the intent-sync and
    /// batch-management hosted services.
    /// </summary>
    public static IServiceCollection AddNArkDelegator(
        this IServiceCollection services, Action<DelegatorOptions> configure)
    {
        services.Configure(configure);
        services.AddSingleton<DelegateeService>();
        services.AddGrpc().AddJsonTranscoding();
        return services;
    }

    /// <summary>Maps the delegator gRPC service (REST is exposed automatically via JSON transcoding).</summary>
    public static IEndpointRouteBuilder MapNArkDelegator(this IEndpointRouteBuilder endpoints)
    {
        endpoints.MapGrpcService<DelegatorGrpcService>();
        return endpoints;
    }
}
