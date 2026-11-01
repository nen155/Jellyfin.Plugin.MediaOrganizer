using MediaBrowser.Model.Tasks;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.MediaOrganizer.Services;

/// <summary>
/// Tarea programada que ejecuta un escaneo completo de todas las carpetas
/// vigiladas (red de seguridad además del FileSystemWatcher).
/// </summary>
public sealed class OrganizeScheduledTask : IScheduledTask, IConfigurableScheduledTask
{
    private readonly ILogger<OrganizeScheduledTask> _logger;
    private readonly OrganizerEngine _engine;

    /// <summary>
    /// Initializes a new instance of the <see cref="OrganizeScheduledTask"/> class.
    /// </summary>
    public OrganizeScheduledTask(ILogger<OrganizeScheduledTask> logger, OrganizerEngine engine)
    {
        _logger = logger;
        _engine = engine;
    }

    /// <inheritdoc />
    public string Name => "Media Organizer: normalizar carpetas vigiladas";

    /// <inheritdoc />
    public string Key => "MediaOrganizerScan";

    /// <inheritdoc />
    public string Description =>
        "Recorre las carpetas vigiladas del plugin Media Organizer y normaliza nombres de series y películas.";

    /// <inheritdoc />
    public string Category => "Library";

    /// <inheritdoc />
    public bool IsHidden => false;

    /// <inheritdoc />
    public bool IsEnabled => true;

    /// <inheritdoc />
    public bool IsLogged => true;

    /// <inheritdoc />
    public Task ExecuteAsync(IProgress<double> progress, CancellationToken cancellationToken)
    {
        var conf = Plugin.Instance?.Configuration;
        var folders = conf?.WatchFolders ?? new List<Configuration.WatchFolder>();
        var total = folders.Count;
        var done = 0;

        foreach (var watch in folders)
        {
            cancellationToken.ThrowIfCancellationRequested();
            try
            {
                var moved = _engine.OrganizeAll(watch);
                if (moved > 0)
                {
                    _logger.LogInformation("[MediaOrganizer] {Path}: {Count} vídeos organizados", watch.Path, moved);
                }
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "[MediaOrganizer] Error escaneando {Path}", watch.Path);
            }

            done++;
            progress.Report(total == 0 ? 100 : done * 100.0 / total);
        }

        return Task.CompletedTask;
    }

    /// <inheritdoc />
    public IEnumerable<TaskTriggerInfo> GetDefaultTriggers()
    {
        // Sin disparadores por defecto: se ejecuta manualmente o al configurarla.
        return Array.Empty<TaskTriggerInfo>();
    }
}
