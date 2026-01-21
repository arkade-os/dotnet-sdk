namespace NArk.Abstractions.Blockchain;

public interface IChainTimeProvider
{
    Task<TimeHeight> GetChainTime(CancellationToken cancellationToken = default);
}