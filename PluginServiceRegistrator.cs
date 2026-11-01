using Jellyfin.Plugin.MediaOrganizer.Services;
using MediaBrowser.Controller;
using MediaBrowser.Controller.Plugins;
using MediaBrowser.Model.Tasks;
using Microsoft.Extensions.DependencyInjection;

namespace Jellyfin.Plugin.MediaOrganizer;

/// <summary>
/// Registra los servicios del plugin en el contenedor DI del servidor.
/// </summary>
public class PluginServiceRegistrator : IPluginServiceRegistrator
{
    /// <inheritdoc />
    public void RegisterServices(IServiceCollection serviceCollection, IServerApplicationHost applicationHost)
    {
        serviceCollection.AddSingleton<OrganizerEngine>();
        serviceCollection.AddHostedService<WatcherHostedService>();
        serviceCollection.AddSingleton<IScheduledTask, OrganizeScheduledTask>();
    }
}
