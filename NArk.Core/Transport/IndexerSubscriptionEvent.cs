namespace NArk.Core.Transport;

/// <summary>Discriminated union for events yielded by <see cref="IClientTransport.OpenSubscriptionStreamAsync"/>.</summary>
public abstract record VtxoSubscriptionEvent;

/// <summary>
/// First event on every new subscription stream. Carries the server-assigned subscription ID
/// that the caller must store for subsequent <see cref="IClientTransport.UpdateSubscriptionScriptsAsync"/> calls
/// and for reconnecting via <see cref="IClientTransport.OpenSubscriptionStreamAsync"/>.
/// </summary>
public record VtxoSubscriptionStarted(string SubscriptionId) : VtxoSubscriptionEvent;

/// <summary>Scripts whose VTXOs changed since the last push.</summary>
public record VtxoScriptsChanged(HashSet<string> Scripts) : VtxoSubscriptionEvent;
