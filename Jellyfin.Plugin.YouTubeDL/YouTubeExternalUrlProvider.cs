using System.Collections.Generic;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.Providers;
using MediaBrowser.Model.Entities;

namespace Jellyfin.Plugin.YouTubeDL;

/// <summary>
/// Provides clickable YouTube links in the Jellyfin UI for items with a YouTube provider ID.
/// </summary>
public class YouTubeExternalUrlProvider : IExternalUrlProvider
{
    /// <inheritdoc />
    public string Name => "YouTube";

    /// <inheritdoc />
    public IEnumerable<string> GetExternalUrls(BaseItem item)
    {
        if (item.TryGetProviderId("youtube", out var youtubeId))
        {
            yield return $"https://www.youtube.com/watch?v={youtubeId}";
        }
    }
}
