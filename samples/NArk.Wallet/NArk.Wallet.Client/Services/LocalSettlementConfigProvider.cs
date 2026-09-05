using System.Text.Json;
using Microsoft.JSInterop;
using NArk.Abstractions;
using NArk.Abstractions.Settlement;

namespace NArk.Wallet.Client.Services;

/// <summary>
/// Sample <see cref="ISettlementConfigProvider"/> backed by browser local storage.
/// <para>
/// The SDK persists no settlement configuration of its own — an application supplies
/// the per-wallet rules it already stores. This wallet keeps a single rule per device,
/// edited on the Settings page.
/// </para>
/// </summary>
public class LocalSettlementConfigProvider(IJSRuntime js)
{
    private const string StorageKey = "arkade.settlement";

    private SettlementRule? _rule;
    private bool _loaded;

    /// <summary>The auto-settlement rule stored on this device.</summary>
    /// <param name="WalletId">Wallet the rule applies to.</param>
    /// <param name="Destination">Arkade or on-chain Bitcoin address to pay out to.</param>
    /// <param name="Threshold">
    /// Balance at which the payout fires, in <paramref name="AssetId"/>'s atomic units —
    /// satoshis when the rule settles BTC.
    /// </param>
    /// <param name="Enabled">Whether the rule is active.</param>
    /// <param name="AssetId">
    /// Arkade asset to settle, or <see langword="null"/> to settle the wallet's BTC balance.
    /// An asset rule measures only that asset's balance and pays out to an Arkade address.
    /// </param>
    public record SettlementRule(
        string WalletId, string Destination, long Threshold, bool Enabled, string? AssetId = null);

    /// <summary>Reads the stored rule, loading it from local storage on first use.</summary>
    public async Task<SettlementRule?> GetRule()
    {
        await EnsureLoaded();
        return _rule;
    }

    /// <summary>Stores <paramref name="rule"/>, or clears the rule when it is <see langword="null"/>.</summary>
    public async Task SaveRule(SettlementRule? rule)
    {
        _rule = rule;
        _loaded = true;

        if (rule is null)
            await js.InvokeVoidAsync("localStorage.removeItem", StorageKey);
        else
            await js.InvokeVoidAsync("localStorage.setItem", StorageKey, JsonSerializer.Serialize(rule));
    }

    /// <inheritdoc cref="ISettlementConfigProvider.GetConfigs" />
    public async Task<IReadOnlyCollection<SettlementConfig>> GetConfigs(
        string? walletId = null,
        CancellationToken cancellationToken = default)
    {
        await EnsureLoaded();

        if (_rule is null || !_rule.Enabled || string.IsNullOrWhiteSpace(_rule.Destination))
            return [];

        if (walletId is not null && _rule.WalletId != walletId)
            return [];

        return
        [
            new SettlementConfig(
                _rule.WalletId,
                ParseDestination(_rule.Destination, _rule.AssetId),
                _rule.Threshold,
                SourceAsset: _rule.AssetId ?? SettlementAssets.Btc)
        ];
    }

    /// <summary>
    /// An <c>ark1…</c> / <c>tark1…</c> address settles off-chain; anything else is
    /// treated as an on-chain Bitcoin address and settles through a collaborative exit.
    /// With <paramref name="assetId"/> set the payout carries that Arkade asset instead of
    /// BTC, which only an Arkade address can receive.
    /// </summary>
    public static SettlementDestination ParseDestination(string address, string? assetId = null)
    {
        if (!string.IsNullOrWhiteSpace(assetId))
            return SettlementDestination.ArkAsset(address, assetId);

        return ArkAddress.TryParse(address, out _)
            ? SettlementDestination.Ark(address)
            : SettlementDestination.BitcoinOnchain(address);
    }

    private async Task EnsureLoaded()
    {
        if (_loaded)
            return;

        try
        {
            var stored = await js.InvokeAsync<string?>("localStorage.getItem", StorageKey);
            if (!string.IsNullOrWhiteSpace(stored))
                _rule = JsonSerializer.Deserialize<SettlementRule>(stored);
        }
        catch
        {
            // A missing or malformed entry just means "no rule configured".
        }

        _loaded = true;
    }
}

/// <summary>
/// Adapts <see cref="LocalSettlementConfigProvider"/> to the SDK's
/// <see cref="ISettlementConfigProvider"/> so the settlement engine can read it.
/// </summary>
public class LocalSettlementConfigAdapter(LocalSettlementConfigProvider provider) : ISettlementConfigProvider
{
    /// <inheritdoc />
    public Task<IReadOnlyCollection<SettlementConfig>> GetConfigs(
        string? walletId = null,
        CancellationToken cancellationToken = default) =>
        provider.GetConfigs(walletId, cancellationToken);
}
