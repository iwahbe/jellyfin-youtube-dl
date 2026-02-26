using MediaBrowser.Controller.Entities.Movies;
using MediaBrowser.Controller.Providers;
using MediaBrowser.Model.Entities;
using MediaBrowser.Model.Providers;

namespace Jellyfin.Plugin.YouTubeDL;

/// <summary>
/// Registers YouTube as an external ID provider so IDs become clickable links.
/// </summary>
public class YouTubeExternalId : IExternalId
{
    /// <inheritdoc />
    public string ProviderName => "YouTube";

    /// <inheritdoc />
    public string Key => "youtube";

    /// <inheritdoc />
    public ExternalIdMediaType? Type => ExternalIdMediaType.Movie;

    /// <inheritdoc />
    public string UrlFormatString => "https://www.youtube.com/watch?v={0}";

    /// <inheritdoc />
    public bool Supports(IHasProviderIds item) => item is Movie;
}
