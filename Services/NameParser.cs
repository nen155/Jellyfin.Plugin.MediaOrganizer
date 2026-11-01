using System.Text.RegularExpressions;

namespace Jellyfin.Plugin.MediaOrganizer.Services;

/// <summary>
/// Información de episodio extraída de un nombre. Season = -1 cuando el nombre
/// solo indica episodio (p.ej. "Cap.5") y hay que resolverla por contexto.
/// </summary>
public sealed record EpisodeInfo(int Season, int Episode);

/// <summary>
/// Parsea nombres estilo tracker ("Cap.101", "S02E05", "1x03", "Temporada 2")
/// y limpia etiquetas basura para obtener títulos legibles por Jellyfin.
/// </summary>
public static class NameParser
{
    private const RegexOptions Options =
        RegexOptions.Compiled | RegexOptions.IgnoreCase | RegexOptions.CultureInvariant;

    // Tokens de episodio, en orden de especificidad.
    private static readonly (Regex Pattern, bool SeasonInMatch)[] EpisodePatterns =
    {
        // S01E02 / s1e5 / S02x03
        (new Regex(@"\bS(\d{1,2})\s*[\._\- ]*[EX](\d{1,3})\b", Options), true),
        // 1x05
        (new Regex(@"(?<!\w)(\d{1,2})\s*x\s*(\d{1,2})(?!\d)", Options), true),
        // Cap.101 / Cap101 / cap(201) -> 3+ dígitos: temporada+episodio
        (new Regex(@"\bC[aá]p[\.\s_]*\(?(\d{3,4})\)?", Options), true),
        // Cap.5 / capitulo 7 -> solo episodio
        (new Regex(@"\bC[aá]p(?:[ií]tulo)?[\.\s_]*(\d{1,2})\b", Options), false),
    };

    // Temporada indicada de forma explícita ("Temporada 2", "Season 02", "T2", "S02").
    private static readonly Regex[] SeasonOnlyPatterns =
    {
        new Regex(@"\b(?:Temporada|Season|Staffel|Saison)\s*(\d{1,2})\b", Options),
        new Regex(@"(?<![\p{L}\p{N}])[TS](\d{1,2})(?![\p{L}\p{N}])", Options),
    };

    private static readonly Regex YearRegex =
        new Regex(@"(?<!\d)((?:19|20)\d{2})(?!\d)", Options);

    private static readonly Regex MultiSpace =
        new Regex(@"[\s_\.]+", Options);

    private static readonly Regex TrailingSeparators =
        new Regex(@"[\s\-\._\[\]\(\)]+$", Options);

    private static readonly Regex TrailingReleaseGroup =
        new Regex(@"-[A-Za-z]*\d[A-Za-z0-9]*$|-[A-Z]{2,}$", Options);

    /// <summary>
    /// Intenta extraer temporada+episodio de un nombre de fichero o carpeta.
    /// </summary>
    /// <param name="name">Nombre sin ruta.</param>
    /// <returns>EpisodeInfo o null si no se reconoce.</returns>
    public static EpisodeInfo? TryParseEpisode(string name)
    {
        foreach (var (pattern, seasonInMatch) in EpisodePatterns)
        {
            var m = pattern.Match(name);
            if (!m.Success)
            {
                continue;
            }

            if (!seasonInMatch)
            {
                return new EpisodeInfo(-1, int.Parse(m.Groups[1].Value));
            }

            if (pattern == EpisodePatterns[2].Pattern)
            {
                // Cap.NNN: últimos 2 dígitos = episodio, el resto = temporada.
                var digits = m.Groups[1].Value;
                var season = int.Parse(digits[..^2]);
                var episode = int.Parse(digits[^2..]);
                return new EpisodeInfo(season, episode);
            }

            return new EpisodeInfo(
                int.Parse(m.Groups[1].Value),
                int.Parse(m.Groups[2].Value));
        }

        return null;
    }

    /// <summary>
    /// Busca una temporada explícita ("Temporada 2", "Season 02", "S02", "T1").
    /// </summary>
    public static int? TryParseSeason(string name)
    {
        foreach (var pattern in SeasonOnlyPatterns)
        {
            var m = pattern.Match(name);
            if (m.Success)
            {
                return int.Parse(m.Groups[1].Value);
            }
        }

        return null;
    }

    /// <summary>
    /// Limpia un nombre: quita el token de episodio/temporada y las etiquetas
    /// basura (patrones configurables), normaliza separadores y devuelve el
    /// título legible ("The.Boys.S05E08..." -> "The Boys").
    /// </summary>
    /// <param name="raw">Nombre en crudo (stem o carpeta).</param>
    /// <param name="stripPatterns">Regex, uno por línea.</param>
    /// <returns>Título limpio (puede quedar vacío).</returns>
    public static string CleanTitle(string raw, IEnumerable<string> stripPatterns)
    {
        var text = Path.GetFileNameWithoutExtension(raw) is { Length: > 0 } stem
            && raw.Contains('.', StringComparison.Ordinal)
            ? stem
            : raw;

        // Cortar en el primer token de episodio/temporada reconocible.
        var cut = int.MaxValue;
        foreach (var (pattern, _) in EpisodePatterns)
        {
            var m = pattern.Match(text);
            if (m.Success && m.Index < cut)
            {
                cut = m.Index;
            }
        }

        foreach (var pattern in SeasonOnlyPatterns)
        {
            var m = pattern.Match(text);
            if (m.Success && m.Index < cut)
            {
                cut = m.Index;
            }
        }

        if (cut != int.MaxValue)
        {
            text = text[..cut];
        }

        foreach (var line in stripPatterns)
        {
            var p = line.Trim();
            if (p.Length == 0)
            {
                continue;
            }

            try
            {
                text = Regex.Replace(text, p, string.Empty, Options);
            }
            catch (ArgumentException)
            {
                // Patrón inválido del usuario: se ignora.
            }
        }

        // Grupo de release final tipo "-CROOKS", "-Me7alh".
        text = TrailingReleaseGroup.Replace(text, string.Empty);
        text = MultiSpace.Replace(text, " ");
        text = TrailingSeparators.Replace(text, string.Empty);
        return text.Trim(' ', '-', '_', '.', '[', ']');
    }

    /// <summary>
    /// Extrae (título, año) de un nombre de película.
    /// </summary>
    public static (string Title, int? Year) ParseMovie(string raw, IEnumerable<string> stripPatterns)
    {
        var yearMatch = YearRegex.Match(raw);
        int? year = yearMatch.Success ? int.Parse(yearMatch.Groups[1].Value) : null;

        var cutText = yearMatch.Success ? raw[..yearMatch.Index] : raw;
        var title = CleanTitle(cutText, stripPatterns);
        if (title.Length == 0)
        {
            title = CleanTitle(raw, stripPatterns);
        }

        return (title, year);
    }

    /// <summary>
    /// Compone el nombre base de episodio según la plantilla configurada.
    /// Tokens: {show}, {ss}, {ee}, {s}, {e}.
    /// </summary>
    public static string FormatEpisodeName(string format, string show, int season, int episode)
        => format
            .Replace("{show}", show, StringComparison.Ordinal)
            .Replace("{ss}", season.ToString("D2"), StringComparison.Ordinal)
            .Replace("{ee}", episode.ToString("D2"), StringComparison.Ordinal)
            .Replace("{s}", season.ToString(), StringComparison.Ordinal)
            .Replace("{e}", episode.ToString(), StringComparison.Ordinal);

    /// <summary>
    /// Compone el nombre de película según la plantilla. Tokens: {title}, {year}.
    /// </summary>
    public static string FormatMovieName(string format, string title, int? year)
    {
        var effective = year.HasValue ? format : "{title}";
        return effective
            .Replace("{title}", title, StringComparison.Ordinal)
            .Replace("{year}", year?.ToString() ?? string.Empty, StringComparison.Ordinal);
    }
}
