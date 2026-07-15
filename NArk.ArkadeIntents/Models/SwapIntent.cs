using NBitcoin;

namespace NArk.Arkade.NonInteractiveSwaps;

public class SwapIntent
{
    public required SwapIntentType Type { get; set; }
    
    public required Money OfferAmount { get; set; }
    public required Money WantAmount { get; set; }
    
    public required SwapIntentStatus Status { get; set; }
    public required DateTimeOffset CreatedAt { get; set; }
}