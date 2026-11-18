using System.Xml;

namespace ParadisERP.Application.Catalog;

/// <summary>
/// Whether an SVG logo is a plain drawing (security review, 2026-10-10, L2). The check it replaces
/// looked for <c>&lt;script</c>, <c>javascript:</c> and <c>on…=</c> as text, which a prefixed
/// <c>&lt;svg:script&gt;</c>, an entity-encoded link or an <c>&lt;animate&gt;</c> that rewrites a
/// link got past. This one parses the file and names what may be in it.
///
/// <para>
/// Elements of the SVG namespace must be drawing elements (shapes, text, gradients, filters,
/// clipping, <c>use</c>); anything else there — <c>script</c>, <c>foreignObject</c>, <c>a</c>,
/// <c>animate</c>, <c>set</c> — refuses the file, and so does any element of the XHTML namespace.
/// Elements of other namespaces (an editor's own: Inkscape, Sodipodi, RDF metadata) are inert
/// and kept. No attribute may be an event handler, a link may only point inside the file (or be an
/// embedded raster image), and no style may load anything from outside.
/// </para>
/// </summary>
public static class SvgSafety
{
    private const string SvgNamespace = "http://www.w3.org/2000/svg";
    private const string XhtmlNamespace = "http://www.w3.org/1999/xhtml";

    private static readonly HashSet<string> Drawing = new(StringComparer.Ordinal)
    {
        "svg", "g", "defs", "symbol", "use", "title", "desc", "metadata", "style",
        "path", "rect", "circle", "ellipse", "line", "polyline", "polygon",
        "text", "tspan", "textPath",
        "linearGradient", "radialGradient", "stop", "pattern", "clipPath", "mask", "marker", "image",
        "filter", "feBlend", "feColorMatrix", "feComponentTransfer", "feComposite", "feDropShadow",
        "feFlood", "feFuncA", "feFuncB", "feFuncG", "feFuncR", "feGaussianBlur", "feMerge",
        "feMergeNode", "feMorphology", "feOffset",
    };

    public const string RefusedMessage =
        "The SVG logo is not a plain drawing (a script, a link, an animation or an outside reference); export it again as a plain drawing, or use PNG.";

    /// <summary>Null for a plain drawing, else why not.</summary>
    public static string? Problem(string svg)
    {
        var settings = new XmlReaderSettings
        {
            // An editor's DOCTYPE line is skipped, never read: no entity is expanded, nothing is fetched.
            DtdProcessing = DtdProcessing.Ignore,
            XmlResolver = null,
            IgnoreComments = true,
            IgnoreProcessingInstructions = true,
        };
        var sawSvg = false;
        try
        {
            using var reader = XmlReader.Create(new StringReader(svg.TrimStart('﻿')), settings);
            while (reader.Read())
            {
                if (reader.NodeType == XmlNodeType.Text || reader.NodeType == XmlNodeType.CDATA)
                {
                    // The text of a <style> element is CSS.
                    if (LoadsFromOutside(reader.Value)) return RefusedMessage;
                    continue;
                }
                if (reader.NodeType != XmlNodeType.Element) continue;

                if (reader.NamespaceURI == XhtmlNamespace) return RefusedMessage;
                if (reader.NamespaceURI == SvgNamespace || reader.NamespaceURI.Length == 0)
                {
                    if (!Drawing.Contains(reader.LocalName)) return RefusedMessage;
                    if (reader.LocalName == "svg") sawSvg = true;
                }

                if (!reader.MoveToFirstAttribute()) continue;
                do
                {
                    var name = reader.LocalName;
                    var value = reader.Value;
                    if (name.StartsWith("on", StringComparison.OrdinalIgnoreCase)) return RefusedMessage;
                    if (name == "href" && !InsideOrEmbedded(value)) return RefusedMessage;
                    if (LoadsFromOutside(value)) return RefusedMessage;
                } while (reader.MoveToNextAttribute());
                reader.MoveToElement();
            }
        }
        catch (XmlException)
        {
            return "The SVG logo cannot be read as XML; export it again, or use PNG.";
        }
        return sawSvg ? null : "The logo's data is not an SVG image.";
    }

    /// <summary>A link to an element of the same file, or an embedded raster image.</summary>
    private static bool InsideOrEmbedded(string href)
    {
        var value = href.Trim();
        return value.Length == 0 || value.StartsWith('#')
            || value.StartsWith("data:image/png", StringComparison.OrdinalIgnoreCase)
            || value.StartsWith("data:image/jpeg", StringComparison.OrdinalIgnoreCase)
            || value.StartsWith("data:image/webp", StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>
    /// A value that would fetch or run something: <c>url(…)</c> to anything but a fragment, an
    /// <c>@import</c>, or a <c>javascript:</c> link however it was spelt (entities are already
    /// decoded by the reader; spaces and case are not counted).
    /// </summary>
    private static bool LoadsFromOutside(string value)
    {
        var compact = new string(value.Where(c => !char.IsWhiteSpace(c)).ToArray()).ToLowerInvariant();
        if (compact.Contains("javascript:") || compact.Contains("@import")) return true;
        for (var at = compact.IndexOf("url(", StringComparison.Ordinal); at >= 0; at = compact.IndexOf("url(", at + 4, StringComparison.Ordinal))
        {
            var inner = compact[(at + 4)..].TrimStart('\'', '"');
            if (!inner.StartsWith('#')) return true;
        }
        return false;
    }
}
