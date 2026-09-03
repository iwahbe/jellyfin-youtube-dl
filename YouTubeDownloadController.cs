using System;
using System.Collections.Concurrent;
using System.ComponentModel.DataAnnotations;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Threading.Tasks;
using System.Xml.Linq;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.Library;
using MediaBrowser.Controller.Providers;
using MediaBrowser.Model.Entities;
using MediaBrowser.Model.IO;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.YouTubeDL;

/// <summary>
/// API controller for YouTube downloads.
/// </summary>
[ApiController]
[Route("api/youtube")]
[Authorize(Policy = "RequiresElevation")]
public class YouTubeDownloadController : ControllerBase
{
    private static readonly ConcurrentDictionary<string, DownloadTask> _tasks = new();
    private readonly ILogger<YouTubeDownloadController> _logger;
    private readonly ILibraryManager _libraryManager;
    private readonly IProviderManager _providerManager;
    private readonly IFileSystem _fileSystem;

    /// <summary>
    /// Initializes a new instance of the <see cref="YouTubeDownloadController"/> class.
    /// </summary>
    public YouTubeDownloadController(
        ILogger<YouTubeDownloadController> logger,
        ILibraryManager libraryManager,
        IProviderManager providerManager,
        IFileSystem fileSystem)
    {
        _logger = logger;
        _libraryManager = libraryManager;
        _providerManager = providerManager;
        _fileSystem = fileSystem;
    }

    /// <summary>
    /// Download a YouTube video.
    /// </summary>
    /// <param name="request">The download request.</param>
    /// <returns>Download status.</returns>
    [HttpPost("download")]
    [ProducesResponseType(StatusCodes.Status200OK)]
    public ActionResult<DownloadResponse> Download([FromBody] DownloadRequest request)
    {
        var taskId = Guid.NewGuid().ToString("N");
        var task = new DownloadTask { Status = "queued", Url = request.Url };
        _tasks[taskId] = task;

        _ = Task.Run(() => ExecuteDownload(taskId, task, request));

        return Ok(new DownloadResponse { TaskId = taskId, Status = "queued" });
    }

    /// <summary>
    /// Get download status.
    /// </summary>
    /// <param name="taskId">The task ID.</param>
    /// <returns>Download status.</returns>
    [HttpGet("status/{taskId}")]
    [ProducesResponseType(StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public ActionResult<DownloadResponse> GetStatus(string taskId)
    {
        if (!_tasks.TryGetValue(taskId, out var task))
        {
            return NotFound();
        }

        return Ok(new DownloadResponse
        {
            TaskId = taskId,
            Status = task.Status,
            Path = task.OutputPath,
            Error = task.Error
        });
    }

    /// <summary>
    /// Fill missing metadata, thumbnail and captions for a library item from YouTube.
    /// </summary>
    /// <param name="itemId">The Jellyfin item ID.</param>
    /// <returns>Task status.</returns>
    [HttpPost("items/{itemId:guid}/refresh")]
    [ProducesResponseType(StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    [ProducesResponseType(StatusCodes.Status409Conflict)]
    public ActionResult<DownloadResponse> Refresh(Guid itemId)
    {
        var item = GetVideo(itemId);
        if (item is null)
        {
            return NotFound();
        }

        if (!item.TryGetProviderId("youtube", out var videoId))
        {
            return Conflict("Item has no YouTube ID. Use link first.");
        }

        return StartTask(item, videoId, task => RefreshItem(task, item, videoId));
    }

    /// <summary>
    /// Replace the video file with a fresh download at the configured quality, then refresh.
    /// </summary>
    /// <param name="itemId">The Jellyfin item ID.</param>
    /// <returns>Task status.</returns>
    [HttpPost("items/{itemId:guid}/redownload")]
    [ProducesResponseType(StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    [ProducesResponseType(StatusCodes.Status409Conflict)]
    public ActionResult<DownloadResponse> Redownload(Guid itemId)
    {
        var item = GetVideo(itemId);
        if (item is null)
        {
            return NotFound();
        }

        if (!item.TryGetProviderId("youtube", out var videoId))
        {
            return Conflict("Item has no YouTube ID. Use link first.");
        }

        return StartTask(item, videoId, task => RedownloadItem(task, item, videoId));
    }

    /// <summary>
    /// Assign a YouTube video ID to a library item, then refresh.
    /// </summary>
    /// <param name="itemId">The Jellyfin item ID.</param>
    /// <param name="request">The video ID to assign.</param>
    /// <returns>Task status.</returns>
    [HttpPost("items/{itemId:guid}/link")]
    [ProducesResponseType(StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public ActionResult<DownloadResponse> Link(Guid itemId, [FromBody] LinkRequest request)
    {
        if (!VideoIdPattern.IsMatch(request.VideoId))
        {
            return BadRequest("VideoId must be an 11 character YouTube video ID.");
        }

        var item = GetVideo(itemId);
        if (item is null)
        {
            return NotFound();
        }

        return StartTask(item, request.VideoId, task => RefreshItem(task, item, request.VideoId));
    }

    private BaseItem? GetVideo(Guid itemId)
    {
        var item = _libraryManager.GetItemById<BaseItem>(itemId);
        return item is Video && !string.IsNullOrEmpty(item.Path) ? item : null;
    }

    private ActionResult<DownloadResponse> StartTask(BaseItem item, string videoId, Func<DownloadTask, Task> work)
    {
        var taskId = Guid.NewGuid().ToString("N");
        var task = new DownloadTask { Status = "queued", Url = WatchUrl(videoId), OutputPath = item.Path };
        _tasks[taskId] = task;

        _ = Task.Run(async () =>
        {
            try
            {
                await work(task);
                task.Status = "complete";
            }
            catch (Exception ex)
            {
                task.Status = "error";
                task.Error = ex.Message;
                _logger.LogError(ex, "Task failed for {Path}", item.Path);
            }
        });

        return Ok(new DownloadResponse { TaskId = taskId, Status = "queued", Path = item.Path });
    }

    private async Task RefreshItem(DownloadTask task, BaseItem item, string videoId)
    {
        var config = Plugin.Instance?.Configuration ?? new PluginConfiguration();
        var url = WatchUrl(videoId);
        var outputBase = Path.ChangeExtension(item.Path, null);

        task.Status = "downloading_metadata";
        var meta = await FetchMetadata(config, url);

        task.Status = "processing_metadata";
        MergeNfo(outputBase + ".nfo", meta, config.DefaultGenre);

        if (!System.IO.File.Exists(outputBase + "-thumb.jpg"))
        {
            await DownloadThumbnail(config, outputBase, url);
        }

        if (Directory.GetFiles(Path.GetDirectoryName(item.Path)!, Path.GetFileName(outputBase) + ".*.srt").Length == 0)
        {
            await RunProcess(config.YtDlpPath, $"{CommonArgs} --skip-download --write-subs --write-auto-subs --convert-subs srt --no-playlist -o \"{outputBase}\" \"{url}\"");
        }

        _providerManager.QueueRefresh(item.Id, new MetadataRefreshOptions(new DirectoryService(_fileSystem))
        {
            MetadataRefreshMode = MetadataRefreshMode.FullRefresh,
            ImageRefreshMode = MetadataRefreshMode.FullRefresh,
            ReplaceAllMetadata = true,
            RemoveOldMetadata = true,
            ForceSave = true,
            IsAutomated = false
        }, RefreshPriority.High);
    }

    private async Task RedownloadItem(DownloadTask task, BaseItem item, string videoId)
    {
        var config = Plugin.Instance?.Configuration ?? new PluginConfiguration();
        var tempFile = Path.ChangeExtension(item.Path, null) + ".redownload.mp4";

        task.Status = "downloading";
        await RunProcess(config.YtDlpPath, $"-f \"{FormatString(config)}\" --merge-output-format mp4 --no-playlist --embed-chapters {CommonArgs} -o \"{tempFile}\" \"{WatchUrl(videoId)}\"");
        System.IO.File.Move(tempFile, item.Path, overwrite: true);

        await RefreshItem(task, item, videoId);
    }

    private async Task ExecuteDownload(string taskId, DownloadTask task, DownloadRequest request)
    {
        var config = Plugin.Instance?.Configuration ?? new PluginConfiguration();
        var outputDir = config.DownloadDirectory;

        try
        {
            task.Status = "downloading_metadata";

            var meta = await FetchMetadata(config, request.Url);
            var (videoId, title, description, uploader, uploadDate, thumbnail) = meta;
            var safeTitle = MakeSafeFilename(title);

            var outputBase = Path.Combine(outputDir, $"{safeTitle} [{videoId}]");
            var outputFile = outputBase + ".mp4";

            // Step 2: Download
            task.Status = "downloading";
            var dlArgs = $"-f \"{FormatString(config)}\" --merge-output-format mp4 --no-playlist --embed-chapters " +
                         "--write-subs --write-auto-subs --convert-subs srt " +
                         $"{CommonArgs} " +
                         $"-o \"{outputFile}\" \"{request.Url}\"";
            await RunProcess(config.YtDlpPath, dlArgs);

            task.OutputPath = outputFile;

            task.Status = "processing_metadata";
            if (!string.IsNullOrEmpty(thumbnail))
            {
                await DownloadThumbnail(config, outputBase, request.Url);
            }

            // Step 4: Generate NFO
            var nfoFile = outputBase + ".nfo";
            GenerateNfo(nfoFile, title, description, uploader, uploadDate, videoId, request.Genre ?? config.DefaultGenre);

            task.Status = "complete";
            _logger.LogInformation("Download complete: {Path}", outputFile);

            // Step 5: Trigger library scan
            try
            {
                _libraryManager.ValidateMediaLibrary(new Progress<double>(), default);
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Failed to trigger library scan");
            }
        }
        catch (Exception ex)
        {
            task.Status = "error";
            task.Error = ex.Message;
            _logger.LogError(ex, "Download failed for {Url}", request.Url);
        }
    }

    private const string CommonArgs = "--js-runtimes deno:/var/lib/jellyfin/.deno/bin/deno --no-mtime";
    private static readonly System.Text.RegularExpressions.Regex VideoIdPattern = new("^[A-Za-z0-9_-]{11}$");

    private static string WatchUrl(string videoId) => "https://www.youtube.com/watch?v=" + videoId;

    private static string FormatString(PluginConfiguration config) =>
        $"bestvideo[height<={config.MaxHeight}][fps<={config.MaxFps}]+bestaudio/best[height<={config.MaxHeight}]";

    private static async Task<VideoMetadata> FetchMetadata(PluginConfiguration config, string url)
    {
        var metaJson = await RunProcess(config.YtDlpPath, $"{CommonArgs} --dump-json --no-playlist \"{url}\"");
        using var doc = JsonDocument.Parse(metaJson);
        var root = doc.RootElement;
        string Get(string name) => root.TryGetProperty(name, out var p) ? p.GetString() ?? "" : "";
        return new VideoMetadata(
            root.GetProperty("id").GetString() ?? "unknown",
            root.GetProperty("title").GetString() ?? "Unknown",
            Get("description"),
            Get("uploader"),
            Get("upload_date"),
            Get("thumbnail"));
    }

    private async Task DownloadThumbnail(PluginConfiguration config, string outputBase, string url)
    {
        try
        {
            // yt-dlp converts the thumbnail in its temp path and then moves it. Keep the
            // temp path on the target volume: a cross-volume move copies file modes, which
            // exFAT rejects.
            var dir = Path.GetDirectoryName(outputBase);
            await RunProcess(config.YtDlpPath, $"{CommonArgs} --skip-download --write-thumbnail --convert-thumbnails jpg -P \"temp:{dir}\" -o \"thumbnail:{outputBase}-thumb\" --no-playlist \"{url}\"");
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to download thumbnail");
        }
    }

    /// <summary>
    /// Set the YouTube ID in the nfo and fill fields that are missing or empty. Existing values are kept.
    /// </summary>
    private static void MergeNfo(string path, VideoMetadata meta, string genre)
    {
        if (!System.IO.File.Exists(path))
        {
            GenerateNfo(path, meta.Title, meta.Description, meta.Uploader, meta.UploadDate, meta.Id, genre);
            return;
        }

        var nfo = XDocument.Load(path);
        var movie = nfo.Root ?? throw new InvalidOperationException($"{path} has no root element");
        var (year, premiered) = SplitUploadDate(meta.UploadDate);

        var uniqueId = movie.Elements("uniqueid").FirstOrDefault(e => (string?)e.Attribute("type") == "youtube");
        if (uniqueId is null)
        {
            movie.Add(new XElement("uniqueid", new XAttribute("type", "youtube"), meta.Id));
        }
        else
        {
            uniqueId.Value = meta.Id;
        }

        FillIfEmpty(movie, "title", meta.Title);
        FillIfEmpty(movie, "plot", meta.Description);
        FillIfEmpty(movie, "studio", meta.Uploader);
        FillIfEmpty(movie, "year", year);
        FillIfEmpty(movie, "premiered", premiered);
        if (!movie.Elements("genre").Any())
        {
            movie.Add(new XElement("genre", genre));
        }

        nfo.Save(path);
    }

    private static void FillIfEmpty(XElement parent, string name, string value)
    {
        if (string.IsNullOrEmpty(value))
        {
            return;
        }

        var element = parent.Element(name);
        if (element is null)
        {
            parent.Add(new XElement(name, value));
        }
        else if (string.IsNullOrWhiteSpace(element.Value))
        {
            element.Value = value;
        }
    }

    private static (string Year, string Premiered) SplitUploadDate(string uploadDate)
    {
        var year = uploadDate.Length >= 4 ? uploadDate[..4] : "";
        var premiered = uploadDate.Length == 8
            ? $"{uploadDate[..4]}-{uploadDate[4..6]}-{uploadDate[6..8]}"
            : "";
        return (year, premiered);
    }

    private static void GenerateNfo(string path, string title, string description, string studio, string uploadDate, string videoId, string genre)
    {
        var (year, premiered) = SplitUploadDate(uploadDate);

        var nfo = new XDocument(
            new XElement("movie",
                new XElement("title", title),
                new XElement("plot", description),
                new XElement("studio", studio),
                new XElement("genre", genre),
                new XElement("year", year),
                new XElement("premiered", premiered),
                new XElement("uniqueid", new XAttribute("type", "youtube"), videoId)));

        nfo.Save(path);
    }

    private static async Task<string> RunProcess(string fileName, string arguments)
    {
        // Ensure yt-dlp path is available
        var env = Environment.GetEnvironmentVariable("PATH") ?? "";
        var home = Environment.GetEnvironmentVariable("HOME") ?? "/home/ink";
        var denoPath = Path.Combine(home, ".deno", "bin");
        if (!env.Contains(denoPath))
        {
            Environment.SetEnvironmentVariable("PATH", denoPath + ":" + env);
        }

        using var process = new Process();
        process.StartInfo = new ProcessStartInfo
        {
            FileName = fileName,
            Arguments = arguments,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true
        };

        process.Start();
        var output = await process.StandardOutput.ReadToEndAsync();
        var error = await process.StandardError.ReadToEndAsync();
        await process.WaitForExitAsync();

        if (process.ExitCode != 0)
        {
            throw new InvalidOperationException($"Process exited with code {process.ExitCode}: {error}");
        }

        return output;
    }

    private static string MakeSafeFilename(string name)
    {
        // Include chars invalid on exFAT/NTFS since Linux doesn't report them
        var invalid = Path.GetInvalidFileNameChars()
            .Concat(new[] { '?', '"', '<', '>', '|', '*', ':' })
            .ToHashSet();
        return new string(name.Select(c => invalid.Contains(c) ? '_' : c).ToArray());
    }
}

/// <summary>
/// Download request body.
/// </summary>
public class DownloadRequest
{
    /// <summary>
    /// Gets or sets the YouTube URL.
    /// </summary>
    [Required]
    public string Url { get; set; } = string.Empty;

    /// <summary>
    /// Gets or sets the collection name.
    /// </summary>
    public string? Collection { get; set; }

    /// <summary>
    /// Gets or sets the genre override.
    /// </summary>
    public string? Genre { get; set; }
}

/// <summary>
/// Link request body.
/// </summary>
public class LinkRequest
{
    /// <summary>
    /// Gets or sets the 11 character YouTube video ID.
    /// </summary>
    [Required]
    public string VideoId { get; set; } = string.Empty;
}

/// <summary>
/// Metadata returned by yt-dlp --dump-json.
/// </summary>
public record VideoMetadata(string Id, string Title, string Description, string Uploader, string UploadDate, string Thumbnail);

/// <summary>
/// Download response.
/// </summary>
public class DownloadResponse
{
    /// <summary>Gets or sets the task ID.</summary>
    public string TaskId { get; set; } = string.Empty;

    /// <summary>Gets or sets the status.</summary>
    public string Status { get; set; } = string.Empty;

    /// <summary>Gets or sets the output path.</summary>
    public string? Path { get; set; }

    /// <summary>Gets or sets the error message.</summary>
    public string? Error { get; set; }
}

/// <summary>
/// Internal download task state.
/// </summary>
public class DownloadTask
{
    /// <summary>Gets or sets the status.</summary>
    public string Status { get; set; } = "queued";

    /// <summary>Gets or sets the URL.</summary>
    public string Url { get; set; } = string.Empty;

    /// <summary>Gets or sets the output path.</summary>
    public string? OutputPath { get; set; }

    /// <summary>Gets or sets the error.</summary>
    public string? Error { get; set; }
}
