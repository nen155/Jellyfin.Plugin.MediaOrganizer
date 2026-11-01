using MediaBrowser.Model.Plugins;

namespace Jellyfin.Plugin.MediaOrganizer.Configuration;

/// <summary>
/// Tipo de contenido de una carpeta vigilada.
/// </summary>
public enum WatchKind
{
    /// <summary>Biblioteca de series (episodios SxxEyy).</summary>
    Series = 0,

    /// <summary>Biblioteca de películas (Título (año)).</summary>
    Peliculas = 1
}

/// <summary>
/// Origen vigilado por el organizador: una biblioteca de Jellyfin
/// (LibraryName) o una ruta del disco (Path).
/// </summary>
public class WatchFolder
{
    /// <summary>
    /// Gets or sets the absolute path watched by the plugin.
    /// Se ignora si LibraryName está informado.
    /// </summary>
    public string Path { get; set; } = string.Empty;

    /// <summary>
    /// Gets or sets the Jellyfin library (virtual folder) name to watch.
    /// Si está informado, se vigilan todas las rutas físicas de la biblioteca.
    /// </summary>
    public string LibraryName { get; set; } = string.Empty;

    /// <summary>Gets or sets the kind of content: Series o Peliculas.</summary>
    public WatchKind Kind { get; set; } = WatchKind.Series;
}

/// <summary>
/// Configuración del plugin Media Organizer.
/// </summary>
public class PluginConfiguration : BasePluginConfiguration
{
    /// <summary>
    /// Patrones (regex, uno por línea) que se eliminan del nombre al limpiarlo.
    /// </summary>
    public const string DefaultStripPatterns =
        @"\[.*?\]
\(.*?(?:www\.|torrent|4k|hd).*?\)
\bwww\.[\w.-]+\b
\b\w+\.(?:com|org|net|io|to|xyz|life|one|tel|tv|me|info|cc)\b
\b(?:HDTV|HDRip|WEB(?:-?DL|-?Rip)?|BluRay|BRRip|BDRip|DVDRip|DVD|480p|576p|720p|1080p|2160p|4K|8K|x264|x265|h\.?26[45]|HEVC|AVC|10bit|8bit|HDR|SDR|AAC|AC3|EAC3|DTS|MP3|FLAC|MULTI|DUAL|VOSTFR|SubS?|Ita|Eng|Spa|Esp|Jap|Castellano|Español|Spanish|English|Latino|FINAL|Proper|REPACK|INTERNAL|COMPLETE|EXTENDED|UNRATED|Remux|TGx|eztv)\b
\b(?:Temporada|Season|Staffel|Saison)\s*\d{1,2}\b
\b[ST]\d{1,2}\b";

    /// <summary>
    /// Patrones (regex, uno por línea) de ficheros basura a eliminar.
    /// </summary>
    public const string DefaultJunkFilePatterns =
        @"\.url$
(?i)downloaded.*\.txt$
(?i)torrent.*\.txt$
(?i)leeme.*\.txt$
(?i)readme.*\.txt$
(?i)sample.*\.(mkv|mp4|avi|m4v)$";

    /// <summary>
    /// Initializes a new instance of the <see cref="PluginConfiguration"/> class.
    /// </summary>
    public PluginConfiguration()
    {
        Enabled = true;
        DryRun = false;
        WatchFolders = new List<WatchFolder>();
        SeasonFolderFormat = "Temporada {0}";
        EpisodeFileFormat = "{show} S{ss}E{ee}";
        MovieNameFormat = "{title} ({year})";
        SettleSeconds = 30;
        MinVideoSizeMb = 20;
        DeleteJunkFiles = true;
        JunkFilePatterns = DefaultJunkFilePatterns;
        JunkDirectoryNames = "Screens;Sample;Samples;Proof;Subs";
        DeleteEpisodeNfo = false;
        DeleteEpisodePosters = false;
        KeepThumbnails = true;
        KeepTrickplay = true;
        RenameSubtitles = true;
        StripPatterns = DefaultStripPatterns;
        QueueLibraryScan = true;
        VideoExtensions = new List<string>
        {
            "avi", "mkv", "mp4", "m4v", "mov", "wmv", "ts", "m2ts", "mpg", "mpeg", "webm", "flv"
        };
        SubtitleExtensions = new List<string>
        {
            "srt", "ass", "ssa", "sub", "vtt", "idx"
        };
    }

    /// <summary>Gets or sets a value indicating whether el organizador está activo.</summary>
    public bool Enabled { get; set; }

    /// <summary>
    /// Gets or sets a value indicating whether solo se registra en el log lo que
    /// se haría, sin tocar el disco.
    /// </summary>
    public bool DryRun { get; set; }

    /// <summary>Gets or sets the watched root folders.</summary>
    public List<WatchFolder> WatchFolders { get; set; }

    /// <summary>
    /// Gets or sets the season folder format. {0} = número de temporada.
    /// Ejemplos: "Temporada {0}", "Season {0}", "Season {0:00}".
    /// </summary>
    public string SeasonFolderFormat { get; set; }

    /// <summary>
    /// Gets or sets the episode file format. Tokens: {show}, {ss} (temporada 2
    /// dígitos), {ee} (episodio 2 dígitos), {s}, {e} (sin ceros).
    /// </summary>
    public string EpisodeFileFormat { get; set; }

    /// <summary>
    /// Gets or sets the movie name format. Tokens: {title}, {year}.
    /// Si no hay año, se usa solo {title}.
    /// </summary>
    public string MovieNameFormat { get; set; }

    /// <summary>
    /// Gets or sets the seconds of quiet time before processing new content
    /// (espera a que termine la copia/descarga).
    /// </summary>
    public int SettleSeconds { get; set; }

    /// <summary>Gets or sets the minimum video size in MB to be organized.</summary>
    public int MinVideoSizeMb { get; set; }

    /// <summary>Gets or sets a value indicating whether se borran ficheros basura.</summary>
    public bool DeleteJunkFiles { get; set; }

    /// <summary>Gets or sets junk file patterns (regex, uno por línea).</summary>
    public string JunkFilePatterns { get; set; }

    /// <summary>Gets or sets junk directory names (separados por ;).</summary>
    public string JunkDirectoryNames { get; set; }

    /// <summary>Gets or sets a value indicating whether se borran los .nfo de episodios.</summary>
    public bool DeleteEpisodeNfo { get; set; }

    /// <summary>
    /// Gets or sets a value indicating whether se borran los "-poster.jpg" a
    /// nivel de episodio (Jellyfin solo usa -thumb en episodios).
    /// </summary>
    public bool DeleteEpisodePosters { get; set; }

    /// <summary>Gets or sets a value indicating whether se conservan "-thumb.jpg".</summary>
    public bool KeepThumbnails { get; set; }

    /// <summary>Gets or sets a value indicating whether se conservan carpetas ".trickplay".</summary>
    public bool KeepTrickplay { get; set; }

    /// <summary>Gets or sets a value indicating whether se renombran subtítulos junto al vídeo.</summary>
    public bool RenameSubtitles { get; set; }

    /// <summary>Gets or sets the name cleaning patterns (regex, uno por línea).</summary>
    public string StripPatterns { get; set; }

    /// <summary>
    /// Gets or sets a value indicating whether se lanza un escaneo de biblioteca
    /// de Jellyfin tras organizar contenido nuevo.
    /// </summary>
    public bool QueueLibraryScan { get; set; }

    /// <summary>Gets or sets the video extensions (sin punto).</summary>
    public List<string> VideoExtensions { get; set; }

    /// <summary>Gets or sets the subtitle extensions (sin punto).</summary>
    public List<string> SubtitleExtensions { get; set; }
}
