// Written by Claude Opus 5.5 (Anthropic) for SecretModel, 2026-11-27, from the design approved by
// Edwin West on 2026-11-27 (Task 9, decision 7). AI-generated.

using SecretModel.Core.Privacy;

namespace SecretModel.Api.Hosting;

/// <summary>
/// Stands between the server's loggers and a place logs go, such as the console (README §3.1,
/// SM-PRIV-005). Every entry, at every level, from every category, passes through it:
/// <list type="bullet">
/// <item>an exception is reduced to its type name, because its message can hold request data;</item>
/// <item>an entry whose text then holds 13 or more decimal digits in a row, ignoring every character
/// that is not a letter or digit (SM-PRIV-002), is replaced by <see cref="Withheld"/>;</item>
/// <item>scopes are dropped, because ASP.NET Core's hold the request path.</item>
/// </list>
/// </summary>
public sealed class GuardedLoggerProvider(ILoggerFactory destination) : ILoggerProvider
{
    public const string Withheld = "log entry withheld";

    public ILogger CreateLogger(string categoryName) => new GuardedLogger(destination.CreateLogger(categoryName));

    public void Dispose() => destination.Dispose();

    private sealed class GuardedLogger(ILogger destination) : ILogger
    {
        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;

        public bool IsEnabled(LogLevel logLevel) => destination.IsEnabled(logLevel);

        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter)
        {
            ArgumentNullException.ThrowIfNull(formatter);
            string text = formatter(state, null);
            if (exception is not null)
            {
                text += $" ({exception.GetType().FullName})";
            }

            if (TokenValidator.HasLongDigitRun(text))
            {
                text = Withheld;
            }

            destination.Log(logLevel, eventId, text, null, static (entry, _) => entry);
        }
    }
}
