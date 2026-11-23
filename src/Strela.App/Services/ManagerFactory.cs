using Strela.Engine;
using Strela.Engine.Manager;

namespace Strela.App.Services;

/// <summary>Единственная точка создания менеджера загрузок.</summary>
public static class ManagerFactory
{
    public static IDownloadManager Create(EngineOptions options, IStrelaLog log) => new DownloadManager(options, log);
}
