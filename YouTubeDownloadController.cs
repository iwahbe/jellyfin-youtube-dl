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
using MediaBrowser.Controller.Library;
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

    /// <summary>
    /// Initializes a new instance of the <see cref="YouTubeDownloadController"/> class.
    /// </summary>
    public YouTubeDownloadController(ILogger<YouTubeDownloadController> logger, ILibraryManager libraryManager)
    {
        _logger = logger;
        _libraryManager = libraryManager;
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

    private async Task ExecuteDownload(string taskId, DownloadTask task, DownloadRequest request)
    {
        var config = Plugin.Instance?.Configuration ?? new PluginConfiguration();
        var outputDir = config.DownloadDirectory;

        try
        {
            task.Status = "downloading_metadata";

            // Step 1: Get metadata via yt-dlp --dump-json
            var denoRuntime = "--js-runtimes deno:/var/lib/jellyfin/.deno/bin/deno";
            var metaJson = await RunProcess(config.YtDlpPath, $"{denoRuntime} --dump-json --no-playlist \"{request.Url}\"");
            using var doc = JsonDocument.Parse(metaJson);
            var root = doc.RootElement;

            var videoId = root.GetProperty("id").GetString() ?? "unknown";
            var title = root.GetProperty("title").GetString() ?? "Unknown";
            var safeTitle = MakeSafeFilename(title);
            var description = root.TryGetProperty("description", out var descProp) ? descProp.GetString() ?? "" : "";
            var uploader = root.TryGetProperty("uploader", out var upProp) ? upProp.GetString() ?? "" : "";
            var uploadDate = root.TryGetProperty("upload_date", out var dateProp) ? dateProp.GetString() ?? "" : "";
            var thumbnail = root.TryGetProperty("thumbnail", out var thumbProp) ? thumbProp.GetString() ?? "" : "";

            var outputBase = Path.Combine(outputDir, $"{safeTitle} [{videoId}]");
            var outputFile = outputBase + ".mp4";

            // Step 2: Download
            task.Status = "downloading";
            var formatStr = $"bestvideo[height<={config.MaxHeight}][fps<={config.MaxFps}]+bestaudio/best[height<={config.MaxHeight}]";
            var dlArgs = $"-f \"{formatStr}\" --merge-output-format mp4 --no-playlist --embed-chapters " +
                         "--write-subs --write-auto-subs --sub-langs \"en.*\" --convert-subs srt " +
                         $"{denoRuntime} " +
                         $"-o \"{outputFile}\" \"{request.Url}\"";
            await RunProcess(config.YtDlpPath, dlArgs);

            task.OutputPath = outputFile;

            // Step 3: Download thumbnail
            task.Status = "processing_metadata";
            var thumbFile = outputBase + "-thumb.jpg";
            if (!string.IsNullOrEmpty(thumbnail))
            {
                try
                {
                    var thumbArgs = $"{denoRuntime} --skip-download --write-thumbnail --convert-thumbnails jpg " +
                                    $"-o \"thumbnail:{outputBase}-thumb\" --no-playlist \"{request.Url}\"";
                    await RunProcess(config.YtDlpPath, thumbArgs);
                }
                catch (Exception ex)
                {
                    _logger.LogWarning(ex, "Failed to download thumbnail");
                }
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

    private static void GenerateNfo(string path, string title, string description, string studio, string uploadDate, string videoId, string genre)
    {
        var year = uploadDate.Length >= 4 ? uploadDate[..4] : "";
        var premiered = uploadDate.Length == 8
            ? $"{uploadDate[..4]}-{uploadDate[4..6]}-{uploadDate[6..8]}"
            : "";

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
