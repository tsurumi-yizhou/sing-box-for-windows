using Grpc.Core;
using SFW.Services.Core;
using Xunit;

namespace SFW.Tests;

/// <summary>
/// Exercises the real signed daemon worker: boxdd authenticates the caller by
/// executable name and signer, so this only runs when the test host itself is
/// signed and SFW_INTEGRATION=1 (the release workflow does both).
/// </summary>
public sealed class ConfigurationValidatorIntegrationTests
{
    [Fact]
    public async Task SignedWorkerAcceptsValidConfigRejectsInvalidAndRecovers()
    {
        Assert.SkipUnless(
            Environment.GetEnvironmentVariable("SFW_INTEGRATION") == "1",
            "Sign the test host and set SFW_INTEGRATION=1 to run the signed-worker check.");

        await ConfigurationValidator.CheckAsync("{\"outbounds\":[{\"type\":\"direct\",\"tag\":\"direct\"}]}", TestContext.Current.CancellationToken);
        await Assert.ThrowsAsync<RpcException>(
            () => ConfigurationValidator.CheckAsync("{\"outbounds\":[{\"type\":\"invalid-protocol\"}]}", TestContext.Current.CancellationToken));
        // A failed check must not poison the next worker/session.
        await ConfigurationValidator.CheckAsync("{}", TestContext.Current.CancellationToken);
    }
}
