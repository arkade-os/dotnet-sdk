using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using NArk.Core.Models.Options;
using NArk.Hosting;

namespace NArk.Tests.Hosting;

/// <summary>
/// A misconfigured batch expiry floor must surface at startup. Without this it would only show up as
/// every intent failing once a batch opened, which reads as a server problem rather than a config one.
/// </summary>
[TestFixture]
public class BatchExpiryOptionsValidationTests
{
    [Test]
    public void DefaultOptions_AreValid()
    {
        Assert.DoesNotThrow(() => Resolve(_ => { }));
    }

    [Test]
    public void LoweredFloor_IsAccepted()
    {
        var options = Resolve(o => o.MinimumExpiry = TimeSpan.FromHours(6));

        Assert.That(options.MinimumExpiry, Is.EqualTo(TimeSpan.FromHours(6)));
    }

    [Test]
    public void ZeroOrNegativeFloor_IsRejected()
    {
        Assert.Multiple(() =>
        {
            Assert.Throws<OptionsValidationException>(() => Resolve(o => o.MinimumExpiry = TimeSpan.Zero));
            Assert.Throws<OptionsValidationException>(() => Resolve(o => o.MinimumExpiry = TimeSpan.FromSeconds(-1)));
            Assert.Throws<OptionsValidationException>(() => Resolve(o => o.MinimumExpiryBlocks = 0));
            Assert.Throws<OptionsValidationException>(() => Resolve(o => o.MinimumExpiryBlocks = -1));
        });
    }

    [Test]
    [TestCase(1)]
    [TestCase(300)]
    [TestCase(511)]
    public void FloorBelowOneGranularityUnit_IsRejectedAtStartup(int floorSeconds)
    {
        // Below 512s the floor rounds down to zero and stops rejecting anything, so it has to fail
        // here rather than look accepted and quietly wave every declared expiry through.
        Assert.Throws<OptionsValidationException>(
            () => Resolve(o => o.MinimumExpiry = TimeSpan.FromSeconds(floorSeconds)));
    }

    [Test]
    public void FloorOfExactlyOneGranularityUnit_IsAccepted()
    {
        var options = Resolve(o => o.MinimumExpiry = TimeSpan.FromSeconds(512));

        Assert.That(options.MinimumExpiry, Is.EqualTo(TimeSpan.FromSeconds(512)));
    }

    private static BatchExpiryOptions Resolve(Action<BatchExpiryOptions> configure)
    {
        // Exercises the same registration AddArkCoreServices uses, without standing up the whole SDK.
        var services = new ServiceCollection();
        services.Configure(configure);
        services.AddBatchExpiryValidation();

        using var provider = services.BuildServiceProvider();
        return provider.GetRequiredService<IOptions<BatchExpiryOptions>>().Value;
    }
}
