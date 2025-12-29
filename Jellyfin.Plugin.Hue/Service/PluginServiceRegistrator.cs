using Jellyfin.Plugin.Hue.Service;
using MediaBrowser.Controller;
using MediaBrowser.Controller.Plugins;
using Microsoft.Extensions.DependencyInjection;

namespace Jellyfin.Plugin.Hue;

public class PluginServiceRegistrator : IPluginServiceRegistrator
{
    public void RegisterServices(IServiceCollection serviceCollection, IServerApplicationHost applicationHost)
    {
        // Register HttpClient with SSL certificate validation bypass for local Hue Bridge
        serviceCollection.AddHttpClient<Hue.HueClient>()
            .ConfigurePrimaryHttpMessageHandler(() => new System.Net.Http.HttpClientHandler
            {
                ServerCertificateCustomValidationCallback = (message, cert, chain, errors) => true
            });

        serviceCollection.AddSingleton<Hue.HueClient>();
        serviceCollection.AddHostedService<HueSyncService>();
    }
}
