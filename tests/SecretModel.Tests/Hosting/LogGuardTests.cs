// Written by Claude Opus 5.5 (Anthropic) for SecretModel, 2026-11-27. AI-generated.
//
// Tests for the log guard (README §3.1, SM-PRIV-005): at every level, an entry that holds 13 or
// more digits in a row (SM-PRIV-002) is withheld, an exception reaches the log as its type only,
// and scopes are dropped. 4111111111111111 is the published Visa test number, used only as an
// input that must never reach a log.

using Microsoft.Extensions.Logging;
using SecretModel.Api.Hosting;

namespace SecretModel.Tests.Hosting;

[TestClass]
public sealed class LogGuardTests
{
    private const string CardNumber = "4111111111111111";

    public static IEnumerable<object[]> Levels =>
        [.. Enum.GetValues<LogLevel>().Where(l => l != LogLevel.None).Select(l => new object[] { l })];

    [TestMethod]
    [TestCategory("SM-PRIV-005")]
    [DynamicData(nameof(Levels))]
    public void AnEntryHoldingAPossibleCardNumber_IsWithheld_AtEveryLevel(LogLevel level)
    {
        (ILogger logger, CapturedLogs logs) = Guarded();

        logger.Log(level, "Field {Value} was refused.", CardNumber);
        logger.Log(level, "Spaced {Value}.", "4111 1111 1111 1111");
        logger.Log(level, "Full-width {Value}.", "４１１１１１１１１１１１１１１１");

        // The text README §3.1 names.
        Assert.HasCount(3, logs.Entries);
        Assert.IsTrue(logs.Entries.All(e => e == $"{level} test: log entry withheld "), string.Join("\n", logs.Entries));
    }

    [TestMethod]
    [TestCategory("SM-PRIV-005")]
    [DynamicData(nameof(Levels))]
    public void AnExceptionReachesTheLogAsItsTypeOnly_AtEveryLevel(LogLevel level)
    {
        (ILogger logger, CapturedLogs logs) = Guarded();

        logger.Log(level, new InvalidOperationException("details " + CardNumber[..8]), "Something failed.");
        logger.Log(level, new FormatException(CardNumber), "Reading failed.");

        Assert.AreEqual(
            $"{level} test: Something failed. (System.InvalidOperationException) \n{level} test: Reading failed. (System.FormatException) ",
            logs.All);
    }

    [TestMethod]
    [TestCategory("SM-PRIV-005")]
    [DynamicData(nameof(Levels))]
    public void AnOrdinaryEntry_PassesUnchanged_AtEveryLevel(LogLevel level)
    {
        (ILogger logger, CapturedLogs logs) = Guarded();

        logger.Log(level, "Twelve digits {Value} pass.", "411111111111");

        Assert.AreEqual($"{level} test: Twelve digits 411111111111 pass. ", logs.All);
    }

    [TestMethod]
    [TestCategory("SM-PRIV-005")]
    public void AnExceptionWhoseEntryHoldsACardNumber_IsWithheldWhole()
    {
        (ILogger logger, CapturedLogs logs) = Guarded();

        logger.LogError(new InvalidOperationException(), "Card {Value}.", CardNumber);

        Assert.AreEqual($"Error test: {GuardedLoggerProvider.Withheld} ", logs.All);
    }

    [TestMethod]
    [TestCategory("SM-PRIV-005")]
    public void Scopes_AreDropped()
    {
        (ILogger logger, CapturedLogs logs) = Guarded();

        using (logger.BeginScope("RequestPath /v1/{Value}", CardNumber))
        {
            logger.LogWarning("Inside a scope.");
        }

        Assert.IsNull(logger.BeginScope("anything"));
        Assert.AreEqual("Warning test: Inside a scope. ", logs.All);
    }

    [TestMethod]
    public void IsEnabled_IsTheDestinations()
    {
        var logs = new CapturedLogs();
        using var provider = new GuardedLoggerProvider(LoggerFactory.Create(b => b.SetMinimumLevel(LogLevel.Warning).AddProvider(logs)));
        ILogger logger = provider.CreateLogger("test");

        Assert.IsFalse(logger.IsEnabled(LogLevel.Information));
        Assert.IsTrue(logger.IsEnabled(LogLevel.Warning));
    }

    private static (ILogger Logger, CapturedLogs Logs) Guarded()
    {
        var logs = new CapturedLogs();
        var provider = new GuardedLoggerProvider(LoggerFactory.Create(b => b.SetMinimumLevel(LogLevel.Trace).AddProvider(logs)));
        return (provider.CreateLogger("test"), logs);
    }
}
