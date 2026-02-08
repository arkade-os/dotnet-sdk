using NBitcoin;

namespace NArk.Blockchain.NBXplorer;

public class ChainTimeProviderOptions
{
    public required Network Network { get; set; }
    public required Uri Uri { get; set; }
}