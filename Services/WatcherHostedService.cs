using Jellyfin.Plugin.MediaOrganizer.Configuration;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.MediaOrganizer.Services;

/// <summary>
/// Servicio alojado que vigila las carpetas configuradas y organiza el
/// contenido nuevo tras un tiempo de asentamiento (para que terminen las
/// copias/descargas antes de tocar nada).
/// </summary>
public sealed class WatcherHostedService : IHostedService, IDisposable
{
    private readonly ILogger<WatcherHostedService> _logger;
    private readonly OrganizerEngine _engine;
    private readonly object _sync = new();
    private readonly List<FileSystemWatcher> _watchers = new();
    private readonly Dictionary<string, Timer> _timers = new(StringComparer.OrdinalIgnoreCase);
    private PluginConfiguration? _appliedConfig;

    /// <summary>
    /// Initializes a new instance of the <see cref="WatcherHostedService"/> class.
    /// </summary>
    public WatcherHostedService(ILogger<WatcherHostedService> logger, OrganizerEngine engine)
    {
        _logger = logger;
        _engine = engine;
    }

    /// <inheritdoc />
    public Task StartAsync(CancellationToken cancellationToken)
    {
        RebuildWatchers();
        _logger.LogInformation("[MediaOrganizer] Vigilancia iniciada");
        return Task.CompletedTask;
    }

    /// <inheritdoc />
    public Task StopAsync(CancellationToken cancellationToken)
    {
        lock (_sync)
        {
            foreach (var w in _watchers)
            {
                w.Dispose();
            }

            _watchers.Clear();
            foreach (var t in _timers.Values)
            {
                t.Dispose();
            }

            _timers.Clear();
        }

        return Task.CompletedTask;
    }

    /// <inheritdoc />
    public void Dispose()
    {
        StopAsync(CancellationToken.None).GetAwaiter().GetResult();
    }

    private void RebuildWatchers()
    {
        var conf = Plugin.Instance?.Configuration;
        if (conf is null)
        {
            return;
        }

        lock (_sync)
        {
            foreach (var w in _watchers)
            {
                w.Dispose();
            }

            _watchers.Clear();
            _appliedConfig = conf;

            foreach (var watch in conf.WatchFolders)
            {
                var roots = _engine.ResolveRoots(watch).ToList();
                if (roots.Count == 0)
                {
                    _logger.LogWarning("[MediaOrganizer] Origen sin rutas válidas: {Path}{Library}",
                        watch.Path, watch.LibraryName.Length > 0 ? " (" + watch.LibraryName + ")" : string.Empty);
                    continue;
                }

                foreach (var root in roots)
                {
                    var fsw = new FileSystemWatcher(root)
                    {
                        IncludeSubdirectories = true,
                        NotifyFilter = NotifyFilters.FileName | NotifyFilters.DirectoryName | NotifyFilters.Size,
                        InternalBufferSize = 64 * 1024,
                    };

                    var capturedWatch = watch;
                    var capturedRoot = root;
                    fsw.Created += (_, e) => OnChange(capturedWatch, capturedRoot, e.FullPath);
                    fsw.Renamed += (_, e) => OnChange(capturedWatch, capturedRoot, e.FullPath);
                    fsw.Changed += (_, e) => OnChange(capturedWatch, capturedRoot, e.FullPath);
                    fsw.Error += (_, _) => OnOverflow(capturedWatch, capturedRoot);
                    fsw.EnableRaisingEvents = true;
                    _watchers.Add(fsw);
                    _logger.LogInformation("[MediaOrganizer] Vigilando {Path} ({Kind})", root, watch.Kind);
                }
            }
        }
    }

    /// <summary>
    /// Reduce el evento al elemento de primer nivel bajo la raíz vigilada, de
    /// modo que una carpeta entera copiada se procese una sola vez.
    /// </summary>
    private static string TopLevelScope(string root, string fullPath)
    {
        var rel = Path.GetRelativePath(root, fullPath);
        var idx = rel.IndexOf(Path.DirectorySeparatorChar);
        return idx < 0 ? fullPath : Path.Combine(root, rel[..idx]);
    }

    private void OnChange(WatchFolder watch, string root, string fullPath)
    {
        try
        {
            // Recargar watchers si la configuración cambió.
            if (!ReferenceEquals(_appliedConfig, Plugin.Instance?.Configuration))
            {
                RebuildWatchers();
                return;
            }

            var conf = Plugin.Instance?.Configuration;
            if (conf is null || !conf.Enabled)
            {
                return;
            }

            var scope = TopLevelScope(root, fullPath);
            var delay = Math.Max(5, conf.SettleSeconds) * 1000;
            var key = "S|" + scope;

            lock (_sync)
            {
                if (_timers.TryGetValue(key, out var existing))
                {
                    existing.Change(delay, Timeout.Infinite);
                    return;
                }

                _timers[key] = new Timer(_ =>
                {
                    lock (_sync)
                    {
                        if (_timers.Remove(key, out var t))
                        {
                            t.Dispose();
                        }
                    }

                    try
                    {
                        var count = _engine.OrganizeScope(watch, root, scope);
                        if (count > 0)
                        {
                            _logger.LogInformation("[MediaOrganizer] Organizados {Count} vídeos en {Scope}", count, scope);
                        }
                    }
                    catch (Exception ex)
                    {
                        _logger.LogError(ex, "[MediaOrganizer] Error organizando {Scope}", scope);
                    }
                }, null, delay, Timeout.Infinite);
            }
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "[MediaOrganizer] Error procesando evento de {Path}", fullPath);
        }
    }

    /// <summary>
    /// Si se desborda el buffer del watcher, hace un escaneo completo de la raíz.
    /// </summary>
    private void OnOverflow(WatchFolder watch, string root)
    {
        var key = "F|" + root;
        lock (_sync)
        {
            if (_timers.ContainsKey(key))
            {
                return;
            }

            _timers[key] = new Timer(_ =>
            {
                lock (_sync)
                {
                    if (_timers.Remove(key, out var t))
                    {
                        t.Dispose();
                    }
                }

                try
                {
                    _logger.LogWarning("[MediaOrganizer] Buffer desbordado; escaneo completo de {Path}", root);
                    _engine.OrganizeScope(watch, root, root);
                }
                catch (Exception ex)
                {
                    _logger.LogError(ex, "[MediaOrganizer] Error en escaneo completo de {Path}", root);
                }
            }, null, 60_000, Timeout.Infinite);
        }
    }
}
