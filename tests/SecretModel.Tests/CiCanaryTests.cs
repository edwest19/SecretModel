// Written by Claude Opus 5.5 (Anthropic) for SecretModel, 2026-11-25. AI-generated.
//
// TEMPORARY. A deliberately failing test that shows CI fails when a test fails (README SM-VER-004).
// It is reverted in the next commit of the same pull request and never reaches main.

namespace SecretModel.Tests;

[TestClass]
public sealed class CiCanaryTests
{
    [TestMethod]
    public void DeliberateFailure_ProvesCiFailsOnAFailingTest()
    {
        Assert.Fail("Deliberate failure for the SM-VER-004 check. This commit is reverted before merge.");
    }
}
