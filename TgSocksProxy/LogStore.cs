using System.Collections.ObjectModel;
using System.Collections.Specialized;

namespace TgSocksProxy;

/// <summary>
/// Потокобезопасное хранилище последних N строк лога.
/// </summary>
public static class LogStore
{
    private const int MaxLines = 100;
    private static readonly object _lock = new();
    private static readonly List<string> _lines = new(MaxLines + 1);

    public static event NotifyCollectionChangedEventHandler? CollectionChanged;

    public static IReadOnlyList<string> Lines
    {
        get { lock (_lock) return _lines.ToArray(); }
    }

    public static void Add(string line)
    {
        NotifyCollectionChangedEventArgs? args = null;
        lock (_lock)
        {
            if (_lines.Count >= MaxLines)
                _lines.RemoveAt(0);
            _lines.Add(line);
            args = new NotifyCollectionChangedEventArgs(NotifyCollectionChangedAction.Add, line);
        }
        CollectionChanged?.Invoke(null, args);
    }
}
