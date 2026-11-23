using Strela.Engine;

namespace Strela.Engine.Tests;

/// <summary>Test log that records every entry in memory.</summary>
public sealed class ListLog : IStrelaLog
{
    private readonly List<(string Level, string Message)> _entries = [];

    public IReadOnlyList<(string Level, string Message)> Entries
    {
        get { lock (_entries) return _entries.ToArray(); }
    }

    public bool Contains(string level, string substring)
    {
        lock (_entries)
            return _entries.Any(e => e.Level == level && e.Message.Contains(substring, StringComparison.Ordinal));
    }

    public void Info(string m) => Add("Info", m);
    public void Warn(string m) => Add("Warn", m);
    public void Error(string m, Exception? ex = null) => Add("Error", ex is null ? m : $"{m}: {ex.Message}");

    private void Add(string level, string message)
    {
        lock (_entries) _entries.Add((level, message));
    }
}
