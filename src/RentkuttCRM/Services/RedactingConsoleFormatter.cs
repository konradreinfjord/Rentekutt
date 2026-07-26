using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Logging.Console;

namespace RentkuttCRM.Services;

/// <summary>
/// Console-formatter som kjører all logg-output gjennom <see cref="FnrRedactor"/> før den
/// skrives. Fungerer som et globalt sikkerhetsnett: selv om fødselsnummer utilsiktet skulle
/// havne i en loggmelding eller exception, maskeres det før det når stdout / Azure Log Stream.
/// </summary>
public sealed class RedactingConsoleFormatter : ConsoleFormatter
{
    public const string FormatterNavn = "redacting";

    public RedactingConsoleFormatter() : base(FormatterNavn) { }

    public override void Write<TState>(in LogEntry<TState> logEntry, IExternalScopeProvider? scopeProvider, TextWriter textWriter)
    {
        var melding = logEntry.Formatter(logEntry.State, logEntry.Exception);
        if (string.IsNullOrEmpty(melding) && logEntry.Exception is null) return;

        textWriter.Write(logEntry.LogLevel switch
        {
            LogLevel.Trace => "trce",
            LogLevel.Debug => "dbug",
            LogLevel.Information => "info",
            LogLevel.Warning => "warn",
            LogLevel.Error => "fail",
            LogLevel.Critical => "crit",
            _ => "info",
        });
        textWriter.Write(": ");
        textWriter.Write(logEntry.Category);
        textWriter.Write('[');
        textWriter.Write(logEntry.EventId.Id);
        textWriter.Write(']');
        textWriter.Write(Environment.NewLine);
        textWriter.Write("      ");
        textWriter.Write(FnrRedactor.Redact(melding));

        if (logEntry.Exception is not null)
        {
            textWriter.Write(Environment.NewLine);
            textWriter.Write(FnrRedactor.Redact(logEntry.Exception.ToString()));
        }
        textWriter.Write(Environment.NewLine);
    }
}
