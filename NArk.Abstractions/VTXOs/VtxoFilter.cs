using NBitcoin;

namespace NArk.Abstractions.VTXOs;

/// <summary>
/// Filter parameters for querying VTXOs.
/// All properties are optional - unset properties don't filter.
/// </summary>
public record VtxoFilter
{
    /// <summary>Filter by script hex strings. If null, no script filter applied.</summary>
    public IReadOnlyCollection<string>? Scripts { get; init; }

    /// <summary>Filter by specific outpoints. If null, no outpoint filter applied.</summary>
    public IReadOnlyCollection<OutPoint>? Outpoints { get; init; }

    /// <summary>Filter by wallet IDs (requires join with contracts table). If null, no wallet filter applied.</summary>
    public string[]? WalletIds { get; init; }

    /// <summary>Include spent VTXOs. Default: false (unspent only).</summary>
    public bool IncludeSpent { get; init; }

    /// <summary>Include recoverable (swept) VTXOs. Default: true.</summary>
    public bool IncludeRecoverable { get; init; } = true;

    /// <summary>Search text for TransactionId or Script. If null, no text search.</summary>
    public string? SearchText { get; init; }

    /// <summary>Number of records to skip (for pagination). If null, no skip.</summary>
    public int? Skip { get; init; }

    /// <summary>Number of records to take (for pagination). If null, no limit.</summary>
    public int? Take { get; init; }

    // Static factory methods for common filter combinations

    /// <summary>Get all unspent VTXOs (default filter).</summary>
    public static VtxoFilter Unspent => new();

    /// <summary>Get all VTXOs including spent.</summary>
    public static VtxoFilter All => new() { IncludeSpent = true };

    /// <summary>Get a specific VTXO by outpoint.</summary>
    public static VtxoFilter ByOutpoint(OutPoint outpoint) =>
        new() { Outpoints = [outpoint], IncludeSpent = true };

    /// <summary>Get unspent VTXOs for specific scripts.</summary>
    public static VtxoFilter ByScripts(IReadOnlyCollection<string> scripts) =>
        new() { Scripts = scripts };

    /// <summary>Get unspent VTXOs for specific scripts (params overload).</summary>
    public static VtxoFilter ByScripts(params string[] scripts) =>
        new() { Scripts = scripts };

    /// <summary>Get VTXOs for specific wallet(s).</summary>
    public static VtxoFilter ByWallet(params string[] walletIds) =>
        new() { WalletIds = walletIds };
}
