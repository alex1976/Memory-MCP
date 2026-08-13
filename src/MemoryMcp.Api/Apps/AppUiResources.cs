using System.ComponentModel;
using ModelContextProtocol.Extensions.Apps;
using ModelContextProtocol.Server;

namespace MemoryMcp.Api.Apps;

/// <summary>
/// Serves the static HTML/JS for the four MCP Apps widgets (Phase 3). Each resource is read
/// straight off disk rather than embedded, mirroring the SDK's own
/// <c>samples/WeatherAppServer/WeatherResources.cs</c> pattern, so the widget markup can be edited
/// without recompiling.
/// </summary>
[McpServerResourceType]
public sealed class AppUiResources
{
    private static readonly string UiDir = Path.Combine(AppContext.BaseDirectory, "Apps", "ui");

    [McpServerResource(UriTemplate = "ui://select-space", Name = "select-space-ui", MimeType = McpApps.HtmlMimeType)]
    [Description("Interactive picker to choose which accessible space is active.")]
    public static string SelectSpaceUi() => ReadUi("select-space.html");

    [McpServerResource(UriTemplate = "ui://guided-save", Name = "guided-save-ui", MimeType = McpApps.HtmlMimeType)]
    [Description("Editable memory draft form with a space selector, before saving.")]
    public static string GuidedSaveUi() => ReadUi("guided-save.html");

    [McpServerResource(UriTemplate = "ui://upload-file", Name = "upload-file-ui", MimeType = McpApps.HtmlMimeType)]
    [Description("Local file picker and upload form for creating a new document.")]
    public static string UploadFileUi() => ReadUi("upload-file.html");

    [McpServerResource(UriTemplate = "ui://memory-graph", Name = "memory-graph-ui", MimeType = McpApps.HtmlMimeType)]
    [Description("Interactive graph visualization of a space's documents and memories.")]
    public static string MemoryGraphUi() => ReadUi("memory-graph.html");

    private static string ReadUi(string fileName) => File.ReadAllText(Path.Combine(UiDir, fileName));
}
