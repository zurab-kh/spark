namespace Strela.Engine;

public interface IStrelaLog
{
    void Info(string m);
    void Warn(string m);
    void Error(string m, Exception? ex = null);
}

public sealed class NullLog : IStrelaLog
{
    public static readonly NullLog Instance = new();

    private NullLog() { }

    public void Info(string m) { }
    public void Warn(string m) { }
    public void Error(string m, Exception? ex = null) { }
}
