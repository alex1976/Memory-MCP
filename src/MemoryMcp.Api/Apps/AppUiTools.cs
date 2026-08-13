using System.ComponentModel;
using ModelContextProtocol.Extensions.Apps;
using ModelContextProtocol.Server;

namespace MemoryMcp.Api.Apps;

/// <summary>
/// Tools that exist solely to open a widget's UI, via <see cref="McpAppUiAttribute"/>. Each widget
/// then drives the real work through ordinary tool calls (<c>setActiveSpace</c>, <c>add_memory</c>,
/// <c>create_document</c>) issued from inside the iframe over the MCP Apps postMessage bridge —
/// these tools return only a short confirmation string, matching the SDK's own
/// <c>samples/WeatherAppServer/WeatherTools.WeatherUi</c>.
/// </summary>
[McpServerToolType]
public sealed class AppUiTools
{
    [McpServerTool(Name = "select_space_ui")]
    [McpAppUi(ResourceUri = "ui://select-space")]
    [Description("Opens a picker to choose which of the current API key's accessible spaces is active.")]
    public static string SelectSpaceUi() => "Showing the space picker.";

    [McpServerTool(Name = "guided_save_ui")]
    [McpAppUi(ResourceUri = "ui://guided-save")]
    [Description("Opens an editable memory draft form with a space selector before saving.")]
    public static string GuidedSaveUi() => "Showing the guided save form.";

    [McpServerTool(Name = "upload_file_ui")]
    [McpAppUi(ResourceUri = "ui://upload-file")]
    [Description("Opens a local file picker to upload a document (text/Markdown/CSV content in this version).")]
    public static string UploadFileUi() => "Showing the file upload form.";

    [McpServerTool(Name = "memory_graph_ui")]
    [McpAppUi(ResourceUri = "ui://memory-graph")]
    [Description("Opens an interactive visualization of the memory graph for the active space.")]
    public static string MemoryGraphUi() => "Showing the memory graph.";
}
