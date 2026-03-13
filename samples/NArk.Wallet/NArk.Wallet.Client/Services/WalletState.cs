namespace NArk.Wallet.Client.Services;

public class WalletState
{
    public string? ActiveWalletId { get; private set; }
    public long BalanceSats { get; private set; }
    public event Action? OnChange;

    public void SetActiveWallet(string walletId)
    {
        ActiveWalletId = walletId;
        OnChange?.Invoke();
    }

    public void UpdateBalance(long sats)
    {
        BalanceSats = sats;
        OnChange?.Invoke();
    }

    public void NotifyChanged() => OnChange?.Invoke();
}
