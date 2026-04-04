using System;
using System.Collections.Generic;
using MediaBrowser.Common.Configuration;
using MediaBrowser.Common.Plugins;
using MediaBrowser.Model.Plugins;
using MediaBrowser.Model.Serialization;

namespace Jellyfin.Plugin.YouTubeDL;

/// <summary>
/// YouTube DL Plugin for Jellyfin.
/// </summary>
public class Plugin : BasePlugin<PluginConfiguration>, IHasWebPages
{
    /// <summary>
    /// Initializes a new instance of the <see cref="Plugin"/> class.
    /// </summary>
    public Plugin(IApplicationPaths applicationPaths, IXmlSerializer xmlSerializer)
        : base(applicationPaths, xmlSerializer)
    {
        Instance = this;
    }

    /// <inheritdoc />
    public override string Name => "YouTube DL";

    /// <inheritdoc />
    public override Guid Id => Guid.Parse("a7b8c9d0-e1f2-3456-7890-abcdef012345");

    /// <inheritdoc />
    public override string Description => "Download YouTube videos and manage metadata via yt-dlp.";

    /// <summary>
    /// Gets the current plugin instance.
    /// </summary>
    public static Plugin? Instance { get; private set; }

    /// <inheritdoc />
    public IEnumerable<PluginPageInfo> GetPages()
    {
        var ns = GetType().Namespace;
        return new[]
        {
            new PluginPageInfo
            {
                Name = Name,
                EmbeddedResourcePath = ns + ".Configuration.configPage.html"
            },
            new PluginPageInfo
            {
                Name = "YouTube DL Download",
                EmbeddedResourcePath = ns + ".Configuration.downloadPage.html",
                EnableInMainMenu = true,
                MenuSection = "server",
                MenuIcon = "download",
                DisplayName = "YouTube DL"
            },
            new PluginPageInfo
            {
                Name = "YouTube DL Download JS",
                EmbeddedResourcePath = ns + ".Configuration.downloadPage.js"
            }
        };
    }
}
