using MediaBrowser.Model.Plugins;

namespace Jellyfin.Plugin.YouTubeDL;

/// <summary>
/// Plugin configuration.
/// </summary>
public class PluginConfiguration : BasePluginConfiguration
{
    /// <summary>
    /// Gets or sets the path to yt-dlp.
    /// </summary>
    public string YtDlpPath { get; set; } = "yt-dlp";

    /// <summary>
    /// Gets or sets the path to ffmpeg.
    /// </summary>
    public string FfmpegPath { get; set; } = "ffmpeg";

    /// <summary>
    /// Gets or sets the default download directory.
    /// </summary>
    public string DownloadDirectory { get; set; } = "/mnt/sandisk2tb/media/movies";

    /// <summary>
    /// Gets or sets the default genre.
    /// </summary>
    public string DefaultGenre { get; set; } = "YouTube";

    /// <summary>
    /// Gets or sets the max video height.
    /// </summary>
    public int MaxHeight { get; set; } = 1080;

    /// <summary>
    /// Gets or sets the max FPS.
    /// </summary>
    public int MaxFps { get; set; } = 60;
}
