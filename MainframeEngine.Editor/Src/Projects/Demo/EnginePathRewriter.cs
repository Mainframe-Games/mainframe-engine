using System.Xml.Linq;

namespace MainframeEngine.Editor;

/// <summary>Points a game's <c>Directory.Build.props</c> at an engine checkout (same value New Project passes as --engine-path).</summary>
public static class EnginePathRewriter
{
    public static void Rewrite(string directoryBuildProps, string enginePath)
    {
        XDocument document;
        try
        {
            document = XDocument.Load(directoryBuildProps, LoadOptions.PreserveWhitespace);
        }
        catch (Exception e) when (e is IOException or System.Xml.XmlException or UnauthorizedAccessException)
        {
            throw new DemoDownloadException("The demo's Directory.Build.props could not be read: " + e.Message, e);
        }

        var property = document.Descendants().FirstOrDefault(e => e.Name.LocalName == "MainframeEnginePath")
            ?? throw new DemoDownloadException("The demo's Directory.Build.props has no MainframeEnginePath.");
        property.Value = enginePath;
        document.Save(directoryBuildProps, SaveOptions.DisableFormatting);
    }
}
