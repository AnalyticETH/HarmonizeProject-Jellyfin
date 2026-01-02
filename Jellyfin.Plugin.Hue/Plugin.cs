using System;
using System.Collections.Generic;
using Jellyfin.Plugin.Hue.Configuration;
using MediaBrowser.Common.Configuration;
using MediaBrowser.Common.Plugins;
using MediaBrowser.Model.Serialization;
using MediaBrowser.Model.Plugins;
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
            Instance = this;
        }

        public static Plugin? Instance { get; private set; }

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
