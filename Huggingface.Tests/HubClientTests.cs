using Huggingface;
using Xunit;

namespace Huggingface.Tests;

/// <summary>
/// Live Hub tests. They require a real HF token in the <c>HF_TOKEN</c> environment variable
/// (a GitHub Actions secret in CI, or your local <c>.env</c>) and are skipped when it is absent,
/// so the suite stays green on forks / offline without silently passing a real assertion.
/// </summary>
public class HubClientTests
{
    private static string? Token => Environment.GetEnvironmentVariable("HF_TOKEN");

    [Fact]
    public async Task WhoAmI_WithValidToken_ReturnsAccountName()
    {
        Assert.SkipWhen(string.IsNullOrWhiteSpace(Token), "HF_TOKEN not set — skipping live Hub test.");

        using var hub = new HubClient(Token);
        string name = await hub.WhoAmIAsync();

        Assert.False(string.IsNullOrWhiteSpace(name));
    }
}
