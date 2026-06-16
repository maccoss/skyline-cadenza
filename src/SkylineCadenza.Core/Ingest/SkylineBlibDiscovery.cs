#nullable enable

using System.Xml;
using System.Xml.Linq;
using SkylineCadenza.Core.SkylineRpc;

namespace SkylineCadenza.Core.Ingest;

/// <summary>
/// Discovers the BiblioSpec <c>.blib</c> files referenced by a running
/// Skyline document via the JSON-RPC interface.
/// </summary>
/// <remarks>
/// Walks <c>GetSettingsListSelectedItems("Libraries")</c> to enumerate
/// the active libraries, then calls <c>GetSettingsListItem</c> per name
/// to fetch its XML definition. The XML root for a BLIB is one of the
/// <c>bibliospec_*</c> element types; the file is identified by its
/// <c>file_name_hint</c> (or <c>Path</c>) attribute. Relative paths are
/// resolved against the document's directory.
///
/// Non-BLIB libraries (NIST <c>.msp</c>, ChromLib, etc.) are silently
/// skipped; Cadenza falls back to its synthesized RT window for those
/// peptides.
/// </remarks>
public static class SkylineBlibDiscovery
{
    public static async Task<List<string>> DiscoverActiveBlibsAsync(
        SkylineSession session,
        IProgress<string>? progress = null,
        CancellationToken cancellationToken = default)
    {
        var blibPaths = new List<string>();

        string[]? libraryNames;
        try
        {
            libraryNames = await Task.Run(
                () => session.Execute(c => c.GetSettingsListSelectedItems("Libraries")),
                cancellationToken);
        }
        catch (Exception ex)
        {
            progress?.Report(
                $"Skyline: could not enumerate libraries ({ex.Message}). "
                + "Falling back to synthesized RT boundaries.");
            return blibPaths;
        }

        if (libraryNames is null || libraryNames.Length == 0) return blibPaths;

        // Resolve document directory for any library paths stored relative
        // to the document.
        string? docPath = null;
        try
        {
            docPath = await Task.Run(
                () => session.Execute(c => c.GetDocumentPath()),
                cancellationToken);
        }
        catch
        {
            // Best effort. Relative library paths will simply fail to
            // resolve and the library will be skipped.
        }
        string? docDir = !string.IsNullOrEmpty(docPath)
            ? Path.GetDirectoryName(docPath)
            : null;

        foreach (var name in libraryNames)
        {
            cancellationToken.ThrowIfCancellationRequested();
            string? xml;
            try
            {
                xml = await Task.Run(
                    () => session.Execute(c => c.GetSettingsListItem("Libraries", name)),
                    cancellationToken);
            }
            catch
            {
                continue;
            }
            if (string.IsNullOrWhiteSpace(xml)) continue;

            string? blibPath = TryExtractBlibPath(xml!, docDir);
            if (blibPath is null) continue;
            if (!File.Exists(blibPath))
            {
                progress?.Report(
                    $"Skyline: library '{name}' references '{blibPath}' "
                    + "which does not exist on disk. Skipping.");
                continue;
            }
            blibPaths.Add(blibPath);
        }

        return blibPaths;
    }

    /// <summary>
    /// Parse a Skyline library XML definition and return the absolute
    /// path to its <c>.blib</c> file if it is a BiblioSpec library,
    /// otherwise null. Exposed for unit testing.
    /// </summary>
    internal static string? TryExtractBlibPath(string libraryXml, string? docDir)
    {
        try
        {
            var doc = XDocument.Parse(libraryXml);
            var root = doc.Root;
            if (root is null) return null;
            // Match bibliospec_lite_library, bibliospec_library, and any
            // future variant whose element name starts with "bibliospec".
            if (!root.Name.LocalName.StartsWith("bibliospec", StringComparison.OrdinalIgnoreCase))
                return null;
            string? hint = root.Attribute("file_name_hint")?.Value
                          ?? root.Attribute("FilePath")?.Value
                          ?? root.Attribute("Path")?.Value;
            if (string.IsNullOrEmpty(hint)) return null;
            if (!hint.EndsWith(".blib", StringComparison.OrdinalIgnoreCase)) return null;
            if (Path.IsPathRooted(hint)) return hint;
            return docDir is not null ? Path.Combine(docDir, hint) : hint;
        }
        catch (XmlException)
        {
            return null;
        }
    }
}
