using Grpc.Core;
using NArk.Core;

namespace NArk.Tests.End2End.Common;

public class ArkadeFaucetRetryTests
{
    [TestCase(StatusCode.AlreadyExists, "VTXO_ALREADY_REGISTERED (4): d8cab5c6b88a4d5b3a20046696b6718500cf9455f08fce373f65b79a90c260ef:1 already registered", true)]
    [TestCase(StatusCode.AlreadyExists, "VTXO_ALREADY_REGISTERED (40): conflict", false)]
    [TestCase(StatusCode.AlreadyExists, "VTXO_ALREADY_REGISTERED_SUFFIX (4): conflict", false)]
    [TestCase(StatusCode.AlreadyExists, "prefix VTXO_ALREADY_REGISTERED (4): conflict", false)]
    [TestCase(StatusCode.Unknown, "VTXO_ALREADY_REGISTERED (4): conflict", false)]
    [TestCase(StatusCode.AlreadyExists, "VTXO_ALREADY_REGISTERED (4): garbage", false)]
    [TestCase(StatusCode.AlreadyExists, "VTXO_ALREADY_REGISTERED (4): d8cab5c6b88a4d5b3a20046696b6718500cf9455f08fce373f65b79a90c260ef:x already registered", false)]
    [TestCase(StatusCode.AlreadyExists, "VTXO_ALREADY_REGISTERED (4): d8cab5c6b88a4d5b3a20046696b6718500cf9455f08fce373f65b79a90c260ef:1 already registered garbage", false)]
    [TestCase(StatusCode.AlreadyExists, "VTXO_ALREADY_REGISTERED (4): D8CAB5C6B88A4D5B3A20046696B6718500CF9455F08FCE373F65B79A90C260EF:1 already registered", false)]
    [TestCase(StatusCode.AlreadyExists, "VTXO_ALREADY_REGISTERED (4): d8cab5c6b88a4d5b3a20046696b6718500cf9455f08fce373f65b79a90c260ef:1 already registered\n", false)]
    [TestCase(StatusCode.AlreadyExists, "VTXO_ALREADY_REGISTERED", false)]
    [TestCase(StatusCode.Unknown, "VTXO_ALREADY_REGISTERED", false)]
    [TestCase(StatusCode.InvalidArgument, "VTXO_ALREADY_REGISTERED", false)]
    [TestCase(StatusCode.AlreadyExists, "another conflict", false)]
    [TestCase(StatusCode.AlreadyExists, "VTXO_ALREADY_REGISTERED_SUFFIX", false)]
    [TestCase(StatusCode.AlreadyExists, "prefix VTXO_ALREADY_REGISTERED", false)]
    [TestCase(StatusCode.AlreadyExists, "vtxo_already_registered", false)]
    [TestCase(StatusCode.AlreadyExists, "", false)]
    [TestCase(StatusCode.FailedPrecondition, "VTXO_RECOVERABLE", true)]
    public void ClassifiesGrpcFundingContention(StatusCode code, string detail, bool retryable)
    {
        var error = new RpcException(new Status(code, detail));
        Assert.That(ArkadeFaucet.IsTransient(error), Is.EqualTo(retryable));
    }

    [Test]
    public void PreservesLocalLockRetry()
    {
        Assert.That(ArkadeFaucet.IsTransient(new AlreadyLockedVtxoException("locked")), Is.True);
        Assert.That(ArkadeFaucet.IsTransient(new InvalidOperationException("VTXO_ALREADY_REGISTERED")), Is.False);
    }
}
