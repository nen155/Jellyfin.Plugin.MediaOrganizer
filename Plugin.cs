using Jellyfin.Plugin.MediaOrganizer.Configuration;
using MediaBrowser.Common.Configuration;
using MediaBrowser.Common.Plugins;
using MediaBrowser.Model.Plugins;
using MediaBrowser.Model.Serialization;

namespace Jellyfin.Plugin.MediaOrganizer;

/// <summary>
/// Plugin principal: normaliza nombres de carpetas y ficheros de series y
/// películas para que Jellyfin pueda obtener sus metadatos.
/// </summary>
public class Plugin : BasePlugin<PluginConfiguration>, IHasWebPages
{
    /// <summary>
    /// Initializes a new instance of the <see cref="Plugin"/> class.
    /// </summary>
    /// <param name="applicationPaths">Rutas de la aplicación.</param>
    /// <param name="xmlSerializer">Serializador XML de configuración.</param>
    public Plugin(IApplicationPaths applicationPaths, IXmlSerializer xmlSerializer)
        : base(applicationPaths, xmlSerializer)
    {
        Instance = this;
    }

    /// <summary>
    /// Gets the current plugin instance.
    /// </summary>
    public static Plugin? Instance { get; private set; }

    /// <inheritdoc />
    public override string Name => "Media Organizer";

    /// <inheritdoc />
    public override string Description =>
        "Normaliza automáticamente nombres de series y películas (Serie SxxEyy, Película (año)) para el scrapeo de metadatos.";

    /// <inheritdoc />
    public override Guid Id => Guid.Parse("9eb40cfc-85d4-4445-88de-21bb90c60a7c");

    /// <inheritdoc />
    public IEnumerable<PluginPageInfo> GetPages()
    {
        return new[]
        {
            new PluginPageInfo
            {
                Name = "MediaOrganizer",
                EmbeddedResourcePath = $"{GetType().Namespace}.Configuration.configPage.html",
                EnableInMainMenu = true,
                MenuSection = "server",
                MenuIcon = "drive_file_rename_outline"
            }
        };
    }
}
