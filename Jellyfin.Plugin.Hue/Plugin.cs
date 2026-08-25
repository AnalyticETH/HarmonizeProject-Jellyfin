using System;
using System.Collections.Generic;
using Jellyfin.Plugin.Hue.Configuration;
using MediaBrowser.Common.Configuration;
using MediaBrowser.Common.Plugins;
using MediaBrowser.Model.Plugins;
using MediaBrowser.Model.Serialization;
using Microsoft.Extensions.DependencyInjection;

namespace Jellyfin.Plugin.Hue
{
    public class Plugin : BasePlugin<PluginConfiguration>, IHasWebPages
    {
        public override string Name => "Philips Hue Sync";
        public override Guid Id => Guid.Parse("4e078f02-ec43-473e-85f1-98e86eb9a761");

        [ActivatorUtilitiesConstructor]
        public Plugin(IApplicationPaths applicationPaths, IXmlSerializer xmlSerializer)
            : base(applicationPaths, xmlSerializer)
        {
            var initialConfiguration = Configuration;
            if (initialConfiguration != null &&
                PluginConfiguration.EnsureUserMappingIds(initialConfiguration.UserMappings))
            {
                try
                {
                    SaveConfiguration();
                }
                catch
                {
                    // A later guarded configuration write can retry the non-secret
                    // identity migration; plugin startup must remain available.
                }
            }

            Instance = this;
        }

        public static Plugin? Instance { get; private set; }

        /// <summary>
        /// Rejects Jellyfin's generic plugin-configuration write route. That route has
        /// no access to the Hue lifecycle gate, import normalization, or credential
        /// preservation contract; accepting its payload would let a raw configuration
        /// replacement race playback and silently clear stored keys. Administrators
        /// must use the guarded <c>/HueSync/Configuration</c> endpoint instead.
        /// </summary>
        public override void UpdateConfiguration(MediaBrowser.Model.Plugins.BasePluginConfiguration configuration)
        {
            throw new InvalidOperationException(
                "Use the authenticated HueSync configuration endpoint for Hue settings.");
        }

        public IEnumerable<PluginPageInfo> GetPages()
        {
            return new[]
            {
                new PluginPageInfo
                {
                    Name = this.Name,
                    EmbeddedResourcePath = GetType().Namespace + ".Configuration.configPage.html"
                }
            };
        }
    }
}
