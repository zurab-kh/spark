namespace Strela.Engine.Storage;

public sealed class DiskFullException(string message, Exception inner) : IOException(message, inner);
