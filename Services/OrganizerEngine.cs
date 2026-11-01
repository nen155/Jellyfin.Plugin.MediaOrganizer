using System.Text.RegularExpressions;
using Jellyfin.Plugin.MediaOrganizer.Configuration;
using MediaBrowser.Controller.Library;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.MediaOrganizer.Services;

/// <summary>
/// Motor que normaliza ficheros y carpetas dentro de las rutas vigiladas:
/// renombra episodios a 'Serie SxxEyy.ext', películas a 'Título (año).ext',
/// mueve adjuntos (-thumb, subtítulos, .trickplay) y elimina la basura.
/// </summary>
public sealed class OrganizerEngine
{
    // Arte/metadatos generados que solo se borran cuando su carpeta queda
    // vacía (restos de un "directorio de episodio" ya desmontado).
    private static readonly Regex EpisodeDirLeftovers = new(
        @"^(?:folder|backdrop|landscape|logo|clearart|disc|banner)\.(?:jpg|jpeg|png)$|^(?:season|tvshow|series)\.nfo$|.*-poster\.(?:jpg|jpeg)$",
        RegexOptions.Compiled | RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);

    private static readonly char[] InvalidChars = Path.GetInvalidFileNameChars();

    private readonly ILogger<OrganizerEngine> _logger;
    private readonly ILibraryManager _libraryManager;

    /// <summary>
    /// Initializes a new instance of the <see cref="OrganizerEngine"/> class.
    /// </summary>
    public OrganizerEngine(ILogger<OrganizerEngine> logger, ILibraryManager libraryManager)
    {
        _logger = logger;
        _libraryManager = libraryManager;
    }

    private static PluginConfiguration Config =>
        Plugin.Instance?.Configuration ?? new PluginConfiguration();

    /// <summary>
    /// Resuelve un origen vigilado a sus rutas físicas: si es una biblioteca de
    /// Jellyfin se devuelven todas sus Locations; si no, su Path.
    /// </summary>
    public IEnumerable<string> ResolveRoots(WatchFolder watch)
    {
        if (!string.IsNullOrWhiteSpace(watch.LibraryName))
        {
            var vf = _libraryManager.GetVirtualFolders()
                .FirstOrDefault(v => string.Equals(v.Name, watch.LibraryName, StringComparison.OrdinalIgnoreCase));
            if (vf is null || vf.Locations.Length == 0)
            {
                _logger.LogWarning("[MediaOrganizer] Biblioteca '{Library}' no encontrada o sin rutas", watch.LibraryName);
                return Array.Empty<string>();
            }

            return vf.Locations.Where(Directory.Exists).ToArray();
        }

        return string.IsNullOrWhiteSpace(watch.Path) || !Directory.Exists(watch.Path)
            ? Array.Empty<string>()
            : new[] { watch.Path };
    }

    /// <summary>
    /// Organiza un ámbito concreto (fichero o carpeta recién llegada).
    /// </summary>
    /// <returns>Número de vídeos movidos.</returns>
    public int OrganizeScope(WatchFolder watch, string root, string scopePath)
    {
        if (!Config.Enabled || !Directory.Exists(root))
        {
            return 0;
        }

        var moved = 0;
        var touchedDirs = new List<string>();

        if (File.Exists(scopePath))
        {
            if (ProcessVideo(new FileInfo(scopePath), watch.Kind, root, touchedDirs))
            {
                moved++;
            }
        }
        else if (Directory.Exists(scopePath))
        {
            foreach (var file in EnumerateVideos(scopePath))
            {
                if (ProcessVideo(file, watch.Kind, root, touchedDirs))
                {
                    moved++;
                }
            }
        }

        CollapseDirs(touchedDirs, root);

        if (moved > 0 && Config.QueueLibraryScan)
        {
            _libraryManager.QueueLibraryScan();
        }

        return moved;
    }

    /// <summary>
    /// Escaneo completo de un origen vigilado (todas sus rutas físicas).
    /// </summary>
    public int OrganizeAll(WatchFolder watch)
    {
        if (!Config.Enabled)
        {
            return 0;
        }

        var moved = 0;
        foreach (var root in ResolveRoots(watch))
        {
            var touchedDirs = new List<string>();
            foreach (var file in EnumerateVideos(root))
            {
                if (ProcessVideo(file, watch.Kind, root, touchedDirs))
                {
                    moved++;
                }
            }

            CollapseDirs(touchedDirs, root);
        }

        if (moved > 0 && Config.QueueLibraryScan)
        {
            _libraryManager.QueueLibraryScan();
        }

        return moved;
    }

    private IEnumerable<FileInfo> EnumerateVideos(string dir)
    {
        var conf = Config;
        IEnumerable<FileInfo> files;
        try
        {
            files = new DirectoryInfo(dir)
                .EnumerateFiles("*", SearchOption.AllDirectories)
                .Where(f => IsVideo(f, conf) && f.Length >= conf.MinVideoSizeMb * 1024L * 1024L)
                .OrderBy(f => f.FullName, StringComparer.OrdinalIgnoreCase)
                .ToList();
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            _logger.LogWarning(ex, "[MediaOrganizer] No se puede enumerar {Dir}", dir);
            return Enumerable.Empty<FileInfo>();
        }

        return files;
    }

    private static bool IsVideo(FileInfo f, PluginConfiguration conf)
        => conf.VideoExtensions.Contains(f.Extension.TrimStart('.'), StringComparer.OrdinalIgnoreCase);

    private bool ProcessVideo(FileInfo video, WatchKind kind, string root, List<string> touchedDirs)
    {
        var conf = Config;
        try
        {
            if (!WaitForStable(video))
            {
                _logger.LogInformation("[MediaOrganizer] {File} sigue copiándose; se omite de momento", video.FullName);
                return false;
            }

            var target = kind == WatchKind.Peliculas
                ? ComputeMovieTarget(video, root)
                : ComputeEpisodeTarget(video, root);

            if (target is null)
            {
                _logger.LogWarning("[MediaOrganizer] No se pudo interpretar {File}; se deja como está", video.FullName);
                return false;
            }

            var (destDir, newStem) = target.Value;
            var dest = Path.Combine(destDir, newStem + video.Extension);

            if (PathsEqual(video.FullName, dest))
            {
                return false; // ya está organizado
            }

            if (File.Exists(dest))
            {
                _logger.LogWarning("[MediaOrganizer] Ya existe {Dest}; no se sobrescribe {Src}", dest, video.FullName);
                return false;
            }

            var oldDir = video.Directory!;
            var oldStem = Path.GetFileNameWithoutExtension(video.Name);

            _logger.LogInformation("[MediaOrganizer] {Src} -> {Dest}", video.FullName, dest);
            if (!conf.DryRun)
            {
                Directory.CreateDirectory(destDir);
                File.Move(video.FullName, dest);
                MoveSidecars(oldDir, oldStem, new DirectoryInfo(destDir), newStem, kind);
            }

            touchedDirs.Add(oldDir.FullName);
            return true;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            _logger.LogWarning(ex, "[MediaOrganizer] Error organizando {File}", video.FullName);
            return false;
        }
    }

    private (string DestDir, string NewStem)? ComputeEpisodeTarget(FileInfo video, string rootPath)
    {
        var conf = Config;
        var strip = SplitLines(conf.StripPatterns);
        var stem = Path.GetFileNameWithoutExtension(video.Name);
        var root = Path.GetFullPath(rootPath);

        // Episodio: del nombre del fichero o de sus carpetas ascendentes.
        var ep = NameParser.TryParseEpisode(stem) ?? ParseEpisodeFromAncestors(video.Directory!, root);
        if (ep is null)
        {
            return null;
        }

        // Serie: del stem o, si queda vacío, de las carpetas ascendentes.
        var show = NameParser.CleanTitle(stem, strip);
        if (show.Length == 0)
        {
            show = CleanTitleFromAncestors(video.Directory!, root, strip);
        }

        if (show.Length == 0)
        {
            return null;
        }

        // Temporada: del episodio, del contexto de carpetas, o 1 por defecto.
        var season = ep.Season > 0
            ? ep.Season
            : ParseSeasonFromAncestors(video.Directory!, root) ?? 1;

        var showDir = Path.Combine(root, Sanitize(show));
        var seasonDir = Path.Combine(showDir, Sanitize(string.Format(conf.SeasonFolderFormat, season)));
        var newStem = Sanitize(NameParser.FormatEpisodeName(conf.EpisodeFileFormat, show, season, ep.Episode));
        return (seasonDir, newStem);
    }

    private (string DestDir, string NewStem)? ComputeMovieTarget(FileInfo video, string rootPath)
    {
        var conf = Config;
        var strip = SplitLines(conf.StripPatterns);
        var stem = Path.GetFileNameWithoutExtension(video.Name);
        var root = Path.GetFullPath(rootPath);

        var (title, year) = NameParser.ParseMovie(stem, strip);
        if (title.Length == 0)
        {
            title = NameParser.CleanTitle(video.Directory!.Name, strip);
            if (title.Length == 0 || year is null)
            {
                var (_, dirYear) = NameParser.ParseMovie(video.Directory!.Name, strip);
                year ??= dirYear;
            }
        }

        if (title.Length == 0)
        {
            return null;
        }

        var name = Sanitize(NameParser.FormatMovieName(conf.MovieNameFormat, title, year));
        var movieDir = Path.Combine(root, name);
        return (movieDir, name);
    }

    private EpisodeInfo? ParseEpisodeFromAncestors(DirectoryInfo dir, string root)
    {
        var d = dir;
        while (d is not null && IsUnder(d.FullName, root) && !PathsEqual(d.FullName, root))
        {
            var ep = NameParser.TryParseEpisode(d.Name);
            if (ep is not null)
            {
                return ep;
            }

            d = d.Parent;
        }

        return null;
    }

    private string CleanTitleFromAncestors(DirectoryInfo dir, string root, string[] strip)
    {
        var d = dir;
        while (d is not null && IsUnder(d.FullName, root) && !PathsEqual(d.FullName, root))
        {
            var title = NameParser.CleanTitle(d.Name, strip);
            if (title.Length > 0)
            {
                return title;
            }

            d = d.Parent;
        }

        return string.Empty;
    }

    private int? ParseSeasonFromAncestors(DirectoryInfo dir, string root)
    {
        var d = dir;
        while (d is not null && IsUnder(d.FullName, root) && !PathsEqual(d.FullName, root))
        {
            var s = NameParser.TryParseSeason(d.Name);
            if (s is not null)
            {
                return s;
            }

            d = d.Parent;
        }

        return null;
    }

    /// <summary>
    /// Mueve (o borra, según configuración) los adjuntos del episodio/película:
    /// -thumb.jpg, -poster.jpg, .nfo, .trickplay y subtítulos.
    /// </summary>
    private void MoveSidecars(DirectoryInfo srcDir, string oldStem, DirectoryInfo destDir, string newStem, WatchKind kind)
    {
        var conf = Config;
        foreach (var entry in srcDir.GetFileSystemInfos())
        {
            if (!entry.Name.StartsWith(oldStem, StringComparison.OrdinalIgnoreCase) || entry.Name.Length == oldStem.Length)
            {
                continue;
            }

            var suffix = entry.Name[oldStem.Length..]; // ".nfo", "-thumb.jpg", ".spa.srt"...
            var isDir = (entry.Attributes & FileAttributes.Directory) != 0;

            if (isDir)
            {
                if (suffix.Equals(".trickplay", StringComparison.OrdinalIgnoreCase))
                {
                    var dest = Path.Combine(destDir.FullName, newStem + suffix);
                    if (conf.KeepTrickplay && !Directory.Exists(dest))
                    {
                        Directory.Move(entry.FullName, dest);
                    }
                    else if (!conf.KeepTrickplay)
                    {
                        Directory.Delete(entry.FullName, true);
                    }
                }

                continue;
            }

            var keep = suffix switch
            {
                var s when s.Equals(".nfo", StringComparison.OrdinalIgnoreCase) => !conf.DeleteEpisodeNfo,
                var s when s.Equals("-thumb.jpg", StringComparison.OrdinalIgnoreCase)
                        || s.Equals("-thumb.jpeg", StringComparison.OrdinalIgnoreCase) => conf.KeepThumbnails,
                var s when s.Equals("-poster.jpg", StringComparison.OrdinalIgnoreCase)
                        || s.Equals("-poster.jpeg", StringComparison.OrdinalIgnoreCase) => !conf.DeleteEpisodePosters,
                var s when IsSubtitle(s, conf) => conf.RenameSubtitles,
                _ => false,
            };

            var isKnown = suffix.Equals(".nfo", StringComparison.OrdinalIgnoreCase)
                || suffix.EndsWith("thumb.jpg", StringComparison.OrdinalIgnoreCase)
                || suffix.EndsWith("thumb.jpeg", StringComparison.OrdinalIgnoreCase)
                || suffix.EndsWith("poster.jpg", StringComparison.OrdinalIgnoreCase)
                || suffix.EndsWith("poster.jpeg", StringComparison.OrdinalIgnoreCase)
                || IsSubtitle(suffix, conf);

            if (!isKnown)
            {
                continue;
            }

            var destFile = Path.Combine(destDir.FullName, newStem + suffix);
            try
            {
                if (keep && !File.Exists(destFile))
                {
                    File.Move(entry.FullName, destFile);
                }
                else if (!keep)
                {
                    File.Delete(entry.FullName);
                }
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                _logger.LogDebug(ex, "[MediaOrganizer] No se pudo mover adjunto {File}", entry.FullName);
            }
        }
    }

    private bool IsSubtitle(string suffix, PluginConfiguration conf)
    {
        var idx = suffix.LastIndexOf('.');
        return idx >= 0
            && conf.SubtitleExtensions.Contains(suffix[(idx + 1)..], StringComparer.OrdinalIgnoreCase);
    }

    /// <summary>
    /// Tras mover los vídeos, sube por las carpetas origen borrando la basura
    /// conocida y colapsando directorios que solo contengan restos.
    /// </summary>
    private void CollapseDirs(IEnumerable<string> touchedDirs, string root)
    {
        var conf = Config;
        if (conf.DryRun)
        {
            return;
        }

        var junkPatterns = conf.DeleteJunkFiles
            ? SplitLines(conf.JunkFilePatterns).Select(p => new Regex(p, RegexOptions.IgnoreCase)).ToList()
            : new List<Regex>();
        var junkDirs = new HashSet<string>(conf.JunkDirectoryNames.Split(';', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries), StringComparer.OrdinalIgnoreCase);

        foreach (var start in touchedDirs.Distinct().OrderByDescending(p => p.Length))
        {
            var dir = new DirectoryInfo(start);
            while (dir.Exists && IsUnder(dir.FullName, root) && !PathsEqual(dir.FullName, root))
            {
                // Basura estricta: siempre se puede borrar.
                if (conf.DeleteJunkFiles)
                {
                    foreach (var f in dir.EnumerateFiles().ToList())
                    {
                        if (junkPatterns.Any(p => p.IsMatch(f.Name)))
                        {
                            TryDeleteFile(f);
                        }
                    }

                    foreach (var d in dir.EnumerateDirectories().ToList())
                    {
                        if (junkDirs.Contains(d.Name))
                        {
                            TryDeleteDir(d);
                        }
                    }
                }

                // Si solo quedan restos generados (folder.jpg, tvshow.nfo, -poster),
                // también se pueden borrar para colapsar la carpeta.
                var remaining = dir.GetFileSystemInfos();
                if (remaining.Length == 0)
                {
                    dir.Parent?.Refresh();
                    var parent = dir.Parent;
                    TryDeleteDir(dir, recursive: false);
                    dir = parent!;
                    continue;
                }

                if (remaining.All(e => EpisodeDirLeftovers.IsMatch(e.Name) || (e is DirectoryInfo d2 && IsEmptyDir(d2))))
                {
                    foreach (var e in remaining)
                    {
                        if (e is FileInfo f)
                        {
                            TryDeleteFile(f);
                        }
                        else if (e is DirectoryInfo d2)
                        {
                            TryDeleteDir(d2);
                        }
                    }

                    var parent = dir.Parent;
                    TryDeleteDir(dir, recursive: false);
                    dir = parent!;
                    continue;
                }

                break;
            }
        }
    }

    private static bool IsEmptyDir(DirectoryInfo d)
    {
        try
        {
            return !d.EnumerateFileSystemInfos().Any();
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return false;
        }
    }

    private void TryDeleteFile(FileInfo f)
    {
        try
        {
            _logger.LogInformation("[MediaOrganizer] Basura eliminada: {File}", f.FullName);
            f.Delete();
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            _logger.LogDebug(ex, "[MediaOrganizer] No se pudo borrar {File}", f.FullName);
        }
    }

    private void TryDeleteDir(DirectoryInfo d, bool recursive = true)
    {
        try
        {
            _logger.LogInformation("[MediaOrganizer] Carpeta eliminada: {Dir}", d.FullName);
            d.Delete(recursive);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            _logger.LogDebug(ex, "[MediaOrganizer] No se pudo borrar {Dir}", d.FullName);
        }
    }

    /// <summary>
    /// Espera a que el fichero termine de copiarse (tamaño estable y abrible en
    /// exclusiva). Máximo ~4 segundos por fichero.
    /// </summary>
    private bool WaitForStable(FileInfo file)
    {
        for (var i = 0; i < 4; i++)
        {
            try
            {
                file.Refresh();
                if (!file.Exists)
                {
                    return false;
                }

                var size = file.Length;
                using (file.Open(FileMode.Open, FileAccess.Read, FileShare.None))
                {
                }

                Thread.Sleep(700);
                file.Refresh();
                if (file.Exists && file.Length == size)
                {
                    return true;
                }
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                Thread.Sleep(700);
            }
        }

        return false;
    }

    private static bool IsUnder(string path, string root)
    {
        var full = Path.GetFullPath(path).TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar;
        var r = Path.GetFullPath(root).TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar;
        return full.StartsWith(r, StringComparison.OrdinalIgnoreCase);
    }

    private static bool PathsEqual(string a, string b)
        => string.Equals(
            Path.GetFullPath(a).TrimEnd(Path.DirectorySeparatorChar),
            Path.GetFullPath(b).TrimEnd(Path.DirectorySeparatorChar),
            StringComparison.OrdinalIgnoreCase);

    private static string Sanitize(string name)
    {
        var chars = name.ToCharArray();
        for (var i = 0; i < chars.Length; i++)
        {
            if (InvalidChars.Contains(chars[i]))
            {
                chars[i] = ' ';
            }
        }

        return new string(chars).Trim();
    }

    private static string[] SplitLines(string text)
        => text.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
}
