using Serilog.Core;
using Serilog.Events;
using Serilog.Formatting;

namespace TgSocksProxy;

/// <summary>
/// Serilog sink, пишущий логи в LogStore для отображения в UI.
/// </summary>
public sealed class LogStoreSink : ILogEventSink
{
    private readonly ITextFormatter _formatter;

    public LogStoreSink(ITextFormatter? formatter = null)
    {
        _formatter = formatter ?? new SimpleFormatter();
    }

    public void Emit(LogEvent logEvent)
    {
        using var writer = new StringWriter();
        _formatter.Format(logEvent, writer);
        LogStore.Add(writer.ToString().TrimEnd('\r', '\n'));
    }
}

internal sealed class SimpleFormatter : ITextFormatter
{
    public void Format(LogEvent logEvent, TextWriter output)
    {
        output.Write($"{logEvent.Timestamp:HH:mm:ss} [{logEvent.Level}] {logEvent.RenderMessage()}");
        if (logEvent.Exception is not null)
            output.Write($" | {logEvent.Exception.Message}");
    }
}
