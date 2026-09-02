using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using System.Xml.Linq;

namespace ProcedurePilot
{
    internal static class LocalDocxPdfConverter
    {
        private static readonly string[] SupportedExtensions = { ".docx", ".docm", ".dotx", ".dotm" };

        internal static bool IsSupported(string path)
        {
            string extension = Path.GetExtension(path);
            return SupportedExtensions.Any(value => string.Equals(value, extension, StringComparison.OrdinalIgnoreCase));
        }

        internal static void Convert(string input, string outputPdf, string browserExecutable)
        {
            if (!File.Exists(input))
                throw new FileNotFoundException(UiText.Get("Le document source est introuvable.", "The source document could not be found."), input);
            if (!IsSupported(input))
                throw new NotSupportedException(UiText.Get(
                    "Le moteur local intégré accepte les documents DOCX, DOCM, DOTX et DOTM.",
                    "The built-in local engine supports DOCX, DOCM, DOTX, and DOTM documents."));
            if (string.IsNullOrWhiteSpace(browserExecutable) || !File.Exists(browserExecutable))
                throw new FileNotFoundException(UiText.Get(
                    "Le moteur PDF local Windows est introuvable.",
                    "The local Windows PDF engine could not be found."), browserExecutable);

            string outputFolder = Path.GetDirectoryName(Path.GetFullPath(outputPdf));
            if (string.IsNullOrWhiteSpace(outputFolder))
                throw new InvalidOperationException(UiText.Get("Le dossier PDF de destination est invalide.", "The destination PDF folder is invalid."));
            Directory.CreateDirectory(outputFolder);

            string workFolder = Path.Combine(outputFolder, "html-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(workFolder);
            try
            {
                string htmlPath = Path.Combine(workFolder, "document.html");
                string profilePath = Path.Combine(workFolder, "browser-profile");
                Directory.CreateDirectory(profilePath);
                File.WriteAllText(htmlPath, BuildHtml(input), new UTF8Encoding(false));

                string arguments = "--headless --no-sandbox --disable-gpu --disable-gpu-compositing --disable-software-rasterizer "
                    + "--disable-extensions --disable-background-networking --no-first-run "
                    + "--allow-file-access-from-files --no-pdf-header-footer --print-to-pdf-no-header "
                    + "--user-data-dir=" + QuoteArgument(profilePath) + " --print-to-pdf=" + QuoteArgument(Path.GetFullPath(outputPdf)) + " "
                    + QuoteArgument(new Uri(htmlPath).AbsoluteUri);
                var start = new ProcessStartInfo(browserExecutable, arguments)
                {
                    UseShellExecute = false,
                    CreateNoWindow = true,
                    RedirectStandardOutput = true,
                    RedirectStandardError = true,
                    WorkingDirectory = workFolder
                };

                using (Process process = Process.Start(start))
                {
                    Task<string> stdoutTask = process.StandardOutput.ReadToEndAsync();
                    Task<string> stderrTask = process.StandardError.ReadToEndAsync();
                    if (!process.WaitForExit(120000))
                    {
                        try { process.Kill(); } catch { }
                        throw new TimeoutException(UiText.Get(
                            "La conversion PDF locale a dépassé deux minutes.",
                            "The local PDF conversion exceeded two minutes."));
                    }

                    string details = (stderrTask.Result + " " + stdoutTask.Result).Trim();
                    if (process.ExitCode != 0)
                        throw new InvalidOperationException(UiText.Format(
                            "Le moteur PDF local a retourné le code {0}. {1}",
                            "The local PDF engine returned code {0}. {1}",
                            process.ExitCode,
                            details));
                }

                if (!File.Exists(outputPdf) || new FileInfo(outputPdf).Length == 0)
                    throw new IOException(UiText.Get(
                        "Le moteur PDF local n’a produit aucun fichier exploitable.",
                        "The local PDF engine did not produce a usable file."));
            }
            finally
            {
                try
                {
                    string fullWork = Path.GetFullPath(workFolder).TrimEnd(Path.DirectorySeparatorChar);
                    string fullParent = Path.GetFullPath(outputFolder).TrimEnd(Path.DirectorySeparatorChar);
                    if (string.Equals(Path.GetDirectoryName(fullWork), fullParent, StringComparison.OrdinalIgnoreCase)
                        && Path.GetFileName(fullWork).StartsWith("html-", StringComparison.Ordinal))
                        Directory.Delete(fullWork, true);
                }
                catch { }
            }
        }

        private static string BuildHtml(string input)
        {
            using (ZipArchive package = ZipFile.OpenRead(input))
            {
                var renderer = new DocxHtmlRenderer(package, Path.GetFileNameWithoutExtension(input));
                return renderer.Render();
            }
        }

        private static string QuoteArgument(string value)
        {
            return "\"" + value.Replace("\"", "\\\"") + "\"";
        }
    }

    internal sealed class DocxHtmlRenderer
    {
        private static readonly XNamespace W = "http://schemas.openxmlformats.org/wordprocessingml/2006/main";
        private static readonly XNamespace R = "http://schemas.openxmlformats.org/officeDocument/2006/relationships";
        private static readonly XNamespace A = "http://schemas.openxmlformats.org/drawingml/2006/main";
        private static readonly XNamespace WP = "http://schemas.openxmlformats.org/drawingml/2006/wordprocessingDrawing";
        private static readonly XNamespace V = "urn:schemas-microsoft-com:vml";
        private static readonly XNamespace PKGREL = "http://schemas.openxmlformats.org/package/2006/relationships";

        private readonly ZipArchive package;
        private readonly XDocument document;
        private readonly StyleCatalog styles;
        private readonly NumberingCatalog numbering;
        private readonly string documentTitle;
        private string currentPart;
        private Dictionary<string, RelationshipInfo> currentRelationships;

        internal DocxHtmlRenderer(ZipArchive package, string documentTitle)
        {
            this.package = package;
            this.documentTitle = string.IsNullOrWhiteSpace(documentTitle) ? "Document" : documentTitle;
            document = LoadXml("word/document.xml");
            if (document == null)
                throw new InvalidDataException(UiText.Get(
                    "Le fichier DOCX ne contient pas word/document.xml.",
                    "The DOCX file does not contain word/document.xml."));
            styles = new StyleCatalog(LoadXml("word/styles.xml"));
            numbering = new NumberingCatalog(LoadXml("word/numbering.xml"));
            SetCurrentPart("word/document.xml");
        }

        internal string Render()
        {
            XElement body = document.Root == null ? null : document.Root.Element(W + "body");
            if (body == null)
                throw new InvalidDataException(UiText.Get(
                    "Le document DOCX ne contient aucun corps de document.",
                    "The DOCX document does not contain a document body."));
            XElement section = body.Descendants(W + "sectPr").LastOrDefault();
            PageSetup page = PageSetup.FromSection(section);

            string bodyHtml = RenderBlocks(body);
            if (string.IsNullOrWhiteSpace(RemoveTags(bodyHtml))) bodyHtml = "<p>&nbsp;</p>";

            string headerHtml = RenderRelatedPart(section, "headerReference");
            string footerHtml = RenderRelatedPart(section, "footerReference");
            string defaultFont = styles.DefaultFont;
            double defaultSize = styles.DefaultFontSize;

            var html = new StringBuilder();
            html.Append("<!doctype html><html lang=\"fr\"><head><meta charset=\"utf-8\"><title>").Append(Html(documentTitle)).Append("</title><style>");
            html.Append("@page{size:").Append(F(page.WidthMm)).Append("mm ").Append(F(page.HeightMm)).Append("mm;margin:")
                .Append(F(page.MarginTopMm)).Append("mm ").Append(F(page.MarginRightMm)).Append("mm ")
                .Append(F(page.MarginBottomMm)).Append("mm ").Append(F(page.MarginLeftMm)).Append("mm;}");
            html.Append("*{box-sizing:border-box;}html,body{margin:0;padding:0;}body{font-family:")
                .Append(CssFont(defaultFont)).Append(";font-size:").Append(F(defaultSize)).Append("pt;line-height:1.15;color:#000;background:#fff;}");
            html.Append("p{margin:0 0 10pt;min-height:1em;}table{border-collapse:collapse;width:100%;margin:0 0 8pt;}td,th{vertical-align:top;padding:2pt 4pt;}img{max-width:100%;height:auto;}a{color:inherit;text-decoration:inherit;}");
            html.Append(".page-break{display:block;break-after:page;page-break-after:always;height:0;}.tab{display:inline-block;width:2.5em;}.list-marker{display:inline-block;min-width:2em;margin-left:-2em;}.page-number::after{content:counter(page);}");
            html.Append("header.doc-header{position:fixed;left:0;right:0;top:-").Append(F(Math.Max(0, page.MarginTopMm - page.HeaderDistanceMm))).Append("mm;height:")
                .Append(F(Math.Max(4, page.MarginTopMm - 2))).Append("mm;overflow:hidden;}footer.doc-footer{position:fixed;left:0;right:0;bottom:-")
                .Append(F(Math.Max(0, page.MarginBottomMm - page.FooterDistanceMm))).Append("mm;height:").Append(F(Math.Max(4, page.MarginBottomMm - 2))).Append("mm;overflow:hidden;}");
            html.Append("</style></head><body>");
            if (!string.IsNullOrWhiteSpace(headerHtml)) html.Append("<header class=\"doc-header\">").Append(headerHtml).Append("</header>");
            if (!string.IsNullOrWhiteSpace(footerHtml)) html.Append("<footer class=\"doc-footer\">").Append(footerHtml).Append("</footer>");
            html.Append("<main>").Append(bodyHtml).Append("</main></body></html>");
            return html.ToString();
        }

        private string RenderRelatedPart(XElement section, string referenceName)
        {
            if (section == null) return "";
            XElement reference = section.Elements(W + referenceName)
                .FirstOrDefault(element => string.Equals(Attr(element, W + "type"), "default", StringComparison.OrdinalIgnoreCase))
                ?? section.Elements(W + referenceName).FirstOrDefault();
            if (reference == null) return "";
            RelationshipInfo relationship;
            if (!currentRelationships.TryGetValue(Attr(reference, R + "id"), out relationship) || relationship.External) return "";
            string part = ResolvePart(currentPart, relationship.Target);
            XDocument related = LoadXml(part);
            if (related == null || related.Root == null) return "";

            string previousPart = currentPart;
            Dictionary<string, RelationshipInfo> previousRelationships = currentRelationships;
            try
            {
                SetCurrentPart(part);
                return RenderBlocks(related.Root);
            }
            finally
            {
                currentPart = previousPart;
                currentRelationships = previousRelationships;
            }
        }

        private string RenderBlocks(XElement parent)
        {
            var html = new StringBuilder();
            foreach (XElement child in parent.Elements())
            {
                if (child.Name == W + "p") html.Append(RenderParagraph(child));
                else if (child.Name == W + "tbl") html.Append(RenderTable(child));
                else if (child.Name == W + "sdt")
                {
                    XElement content = child.Element(W + "sdtContent");
                    if (content != null) html.Append(RenderBlocks(content));
                }
            }
            return html.ToString();
        }

        private string RenderParagraph(XElement paragraph)
        {
            XElement properties = paragraph.Element(W + "pPr");
            string styleId = properties == null ? null : Attr(properties.Element(W + "pStyle"), W + "val");
            string tag = styles.GetParagraphTag(styleId);
            string css = styles.GetParagraphCss(styleId) + ParagraphCss(properties);
            var content = new StringBuilder();

            string marker = numbering.NextMarker(properties);
            if (!string.IsNullOrEmpty(marker)) content.Append("<span class=\"list-marker\">").Append(Html(marker)).Append("</span>");
            foreach (XNode node in paragraph.Nodes())
            {
                XElement element = node as XElement;
                if (element == null || element.Name == W + "pPr") continue;
                content.Append(RenderInline(element));
            }

            if (content.Length == 0) content.Append("&nbsp;");
            return "<" + tag + StyleAttribute(css) + ">" + content + "</" + tag + ">";
        }

        private string RenderInline(XElement element)
        {
            if (element.Name == W + "r") return RenderRun(element);
            if (element.Name == W + "hyperlink")
            {
                string href = "";
                RelationshipInfo relationship;
                if (currentRelationships.TryGetValue(Attr(element, R + "id"), out relationship))
                    href = relationship.External ? relationship.Target : new Uri(ResolvePart(currentPart, relationship.Target), UriKind.Relative).ToString();
                string children = string.Concat(element.Elements().Select(RenderInline).ToArray());
                return href.Length == 0 ? children : "<a href=\"" + Html(href) + "\">" + children + "</a>";
            }
            if (element.Name == W + "smartTag" || element.Name == W + "ins" || element.Name == W + "sdtContent")
                return string.Concat(element.Elements().Select(RenderInline).ToArray());
            if (element.Name == W + "sdt")
            {
                XElement content = element.Element(W + "sdtContent");
                return content == null ? "" : string.Concat(content.Elements().Select(RenderInline).ToArray());
            }
            if (element.Name == W + "fldSimple")
            {
                string instruction = Attr(element, W + "instr");
                if (instruction.IndexOf("PAGE", StringComparison.OrdinalIgnoreCase) >= 0) return "<span class=\"page-number\"></span>";
                return string.Concat(element.Elements().Select(RenderInline).ToArray());
            }
            return "";
        }

        private string RenderRun(XElement run)
        {
            XElement properties = run.Element(W + "rPr");
            string styleId = properties == null ? null : Attr(properties.Element(W + "rStyle"), W + "val");
            string css = styles.GetRunCss(styleId) + RunCss(properties);
            var content = new StringBuilder();
            foreach (XElement child in run.Elements())
            {
                if (child.Name == W + "rPr") continue;
                if (child.Name == W + "t" || child.Name == W + "delText") content.Append(Html(child.Value));
                else if (child.Name == W + "tab") content.Append("<span class=\"tab\">&nbsp;</span>");
                else if (child.Name == W + "br")
                {
                    string type = Attr(child, W + "type");
                    content.Append(string.Equals(type, "page", StringComparison.OrdinalIgnoreCase) ? "<span class=\"page-break\"></span>" : "<br>");
                }
                else if (child.Name == W + "cr") content.Append("<br>");
                else if (child.Name == W + "noBreakHyphen") content.Append("-");
                else if (child.Name == W + "softHyphen") content.Append("&shy;");
                else if (child.Name == W + "drawing" || child.Name == W + "pict") content.Append(RenderImage(child));
                else if (child.Name == W + "instrText" && child.Value.IndexOf("PAGE", StringComparison.OrdinalIgnoreCase) >= 0)
                    content.Append("<span class=\"page-number\"></span>");
            }
            if (content.Length == 0) return "";
            return css.Length == 0 ? content.ToString() : "<span" + StyleAttribute(css) + ">" + content + "</span>";
        }

        private string RenderImage(XElement imageContainer)
        {
            XElement blip = imageContainer.Descendants(A + "blip").FirstOrDefault();
            XElement imageData = imageContainer.Descendants(V + "imagedata").FirstOrDefault();
            string id = blip != null ? Attr(blip, R + "embed") : Attr(imageData, R + "id");
            RelationshipInfo relationship;
            if (string.IsNullOrWhiteSpace(id) || !currentRelationships.TryGetValue(id, out relationship) || relationship.External) return "";
            string part = ResolvePart(currentPart, relationship.Target);
            ZipArchiveEntry entry = GetEntry(part);
            if (entry == null) return "";
            byte[] data;
            using (Stream stream = entry.Open())
            using (var memory = new MemoryStream())
            {
                stream.CopyTo(memory);
                data = memory.ToArray();
            }

            string css = "max-width:100%;";
            XElement extent = imageContainer.Descendants(WP + "extent").FirstOrDefault();
            long cx;
            long cy;
            if (extent != null && long.TryParse(Attr(extent, "cx"), out cx) && long.TryParse(Attr(extent, "cy"), out cy))
                css += "width:" + F(cx / 12700.0) + "pt;height:" + F(cy / 12700.0) + "pt;";

            return "<img alt=\"\" src=\"data:" + MimeType(part) + ";base64," + Convert.ToBase64String(data) + "\"" + StyleAttribute(css) + ">";
        }

        private string RenderTable(XElement table)
        {
            XElement properties = table.Element(W + "tblPr");
            string tableCss = TableCss(properties);
            var html = new StringBuilder("<table" + StyleAttribute(tableCss) + "><tbody>");
            foreach (XElement row in table.Elements(W + "tr"))
            {
                html.Append("<tr");
                XElement rowProperties = row.Element(W + "trPr");
                XElement height = rowProperties == null ? null : rowProperties.Element(W + "trHeight");
                html.Append(StyleAttribute(height == null ? "" : "min-height:" + Twips(Attr(height, W + "val")) + "pt;"));
                html.Append(">");
                foreach (XElement cell in row.Elements(W + "tc"))
                {
                    XElement cellProperties = cell.Element(W + "tcPr");
                    XElement merge = cellProperties == null ? null : cellProperties.Element(W + "vMerge");
                    if (merge != null && string.Equals(Attr(merge, W + "val"), "continue", StringComparison.OrdinalIgnoreCase)) continue;
                    XElement gridSpan = cellProperties == null ? null : cellProperties.Element(W + "gridSpan");
                    string colspan = gridSpan == null ? "" : " colspan=\"" + Html(Attr(gridSpan, W + "val")) + "\"";
                    html.Append("<td").Append(colspan).Append(StyleAttribute(CellCss(cellProperties))).Append(">")
                        .Append(RenderBlocks(cell)).Append("</td>");
                }
                html.Append("</tr>");
            }
            html.Append("</tbody></table>");
            return html.ToString();
        }

        private string RenderBlocksFromElements(IEnumerable<XElement> elements)
        {
            var wrapper = new XElement("wrapper", elements);
            return RenderBlocks(wrapper);
        }

        private string ParagraphCss(XElement properties)
        {
            if (properties == null) return "";
            var css = new StringBuilder();
            string alignment = Attr(properties.Element(W + "jc"), W + "val");
            if (alignment == "both" || alignment == "distribute") alignment = "justify";
            if (alignment == "center" || alignment == "right" || alignment == "justify" || alignment == "left") css.Append("text-align:").Append(alignment).Append(";");
            XElement spacing = properties.Element(W + "spacing");
            if (spacing != null)
            {
                AddTwips(css, "margin-top", Attr(spacing, W + "before"));
                AddTwips(css, "margin-bottom", Attr(spacing, W + "after"));
                string line = Attr(spacing, W + "line");
                double lineValue;
                if (double.TryParse(line, NumberStyles.Any, CultureInfo.InvariantCulture, out lineValue))
                {
                    string rule = Attr(spacing, W + "lineRule");
                    if (rule == "auto") css.Append("line-height:").Append(F(lineValue / 240.0)).Append(";");
                    else css.Append("line-height:").Append(F(lineValue / 20.0)).Append("pt;");
                }
            }
            XElement indent = properties.Element(W + "ind");
            if (indent != null)
            {
                AddTwips(css, "margin-left", Attr(indent, W + "left"));
                AddTwips(css, "margin-right", Attr(indent, W + "right"));
                AddTwips(css, "text-indent", Attr(indent, W + "firstLine"));
                double hanging;
                if (double.TryParse(Attr(indent, W + "hanging"), NumberStyles.Any, CultureInfo.InvariantCulture, out hanging))
                    css.Append("text-indent:-").Append(F(hanging / 20.0)).Append("pt;");
            }
            if (properties.Element(W + "pageBreakBefore") != null) css.Append("break-before:page;page-break-before:always;");
            if (properties.Element(W + "keepNext") != null) css.Append("break-after:avoid-page;page-break-after:avoid;");
            if (properties.Element(W + "keepLines") != null) css.Append("break-inside:avoid;page-break-inside:avoid;");
            AddShading(css, properties.Element(W + "shd"));
            return css.ToString();
        }

        private string RunCss(XElement properties)
        {
            if (properties == null) return "";
            var css = new StringBuilder();
            if (IsOn(properties.Element(W + "b"))) css.Append("font-weight:bold;");
            if (IsOn(properties.Element(W + "i"))) css.Append("font-style:italic;");
            XElement underline = properties.Element(W + "u");
            if (underline != null && Attr(underline, W + "val") != "none") css.Append("text-decoration:underline;");
            if (IsOn(properties.Element(W + "strike"))) css.Append("text-decoration:line-through;");
            string color = Attr(properties.Element(W + "color"), W + "val");
            if (IsHexColor(color)) css.Append("color:#").Append(color).Append(";");
            string highlight = Attr(properties.Element(W + "highlight"), W + "val");
            string highlightColor = NamedColor(highlight);
            if (highlightColor.Length > 0) css.Append("background-color:").Append(highlightColor).Append(";");
            string size = Attr(properties.Element(W + "sz"), W + "val");
            double sizeValue;
            if (double.TryParse(size, NumberStyles.Any, CultureInfo.InvariantCulture, out sizeValue)) css.Append("font-size:").Append(F(sizeValue / 2.0)).Append("pt;");
            XElement fonts = properties.Element(W + "rFonts");
            string font = fonts == null ? "" : (Attr(fonts, W + "ascii") ?? Attr(fonts, W + "hAnsi"));
            if (!string.IsNullOrWhiteSpace(font)) css.Append("font-family:").Append(CssFont(font)).Append(";");
            string vertical = Attr(properties.Element(W + "vertAlign"), W + "val");
            if (vertical == "superscript") css.Append("vertical-align:super;font-size:0.75em;");
            else if (vertical == "subscript") css.Append("vertical-align:sub;font-size:0.75em;");
            if (IsOn(properties.Element(W + "caps"))) css.Append("text-transform:uppercase;");
            if (IsOn(properties.Element(W + "smallCaps"))) css.Append("font-variant:small-caps;");
            return css.ToString();
        }

        private string TableCss(XElement properties)
        {
            var css = new StringBuilder("border-collapse:collapse;");
            if (properties == null) return css.ToString();
            XElement width = properties.Element(W + "tblW");
            if (width != null) css.Append(WidthCss(width));
            string alignment = Attr(properties.Element(W + "jc"), W + "val");
            if (alignment == "center") css.Append("margin-left:auto;margin-right:auto;");
            else if (alignment == "right") css.Append("margin-left:auto;margin-right:0;");
            AddBorders(css, properties.Element(W + "tblBorders"));
            AddShading(css, properties.Element(W + "shd"));
            return css.ToString();
        }

        private string CellCss(XElement properties)
        {
            var css = new StringBuilder();
            if (properties == null) return css.ToString();
            XElement width = properties.Element(W + "tcW");
            if (width != null) css.Append(WidthCss(width));
            string vertical = Attr(properties.Element(W + "vAlign"), W + "val");
            if (vertical == "center") vertical = "middle";
            if (vertical == "top" || vertical == "middle" || vertical == "bottom") css.Append("vertical-align:").Append(vertical).Append(";");
            AddBorders(css, properties.Element(W + "tcBorders"));
            AddShading(css, properties.Element(W + "shd"));
            XElement margins = properties.Element(W + "tcMar");
            if (margins != null)
            {
                AddTwips(css, "padding-top", Attr(margins.Element(W + "top"), W + "w"));
                AddTwips(css, "padding-right", Attr(margins.Element(W + "right"), W + "w"));
                AddTwips(css, "padding-bottom", Attr(margins.Element(W + "bottom"), W + "w"));
                AddTwips(css, "padding-left", Attr(margins.Element(W + "left"), W + "w"));
            }
            return css.ToString();
        }

        private static void AddBorders(StringBuilder css, XElement borders)
        {
            if (borders == null) return;
            foreach (string side in new[] { "top", "right", "bottom", "left", "insideH", "insideV" })
            {
                XElement border = borders.Element(W + side);
                if (border == null) continue;
                string value = Attr(border, W + "val");
                if (value == "nil" || value == "none") continue;
                string color = Attr(border, W + "color");
                if (!IsHexColor(color)) color = "000000";
                double size;
                if (!double.TryParse(Attr(border, W + "sz"), NumberStyles.Any, CultureInfo.InvariantCulture, out size)) size = 4;
                string cssSide = side == "insideH" || side == "insideV" ? "border" : "border-" + side;
                css.Append(cssSide).Append(":").Append(F(Math.Max(0.5, size / 8.0))).Append("pt solid #").Append(color).Append(";");
            }
        }

        private static void AddShading(StringBuilder css, XElement shading)
        {
            string fill = Attr(shading, W + "fill");
            if (IsHexColor(fill)) css.Append("background-color:#").Append(fill).Append(";");
        }

        private static string WidthCss(XElement width)
        {
            string type = Attr(width, W + "type");
            double value;
            if (!double.TryParse(Attr(width, W + "w"), NumberStyles.Any, CultureInfo.InvariantCulture, out value)) return "";
            if (type == "pct") return "width:" + F(value / 50.0) + "%;";
            if (type == "dxa") return "width:" + F(value / 20.0) + "pt;";
            return "";
        }

        private void SetCurrentPart(string part)
        {
            currentPart = NormalizePart(part);
            currentRelationships = LoadRelationships(currentPart);
        }

        private Dictionary<string, RelationshipInfo> LoadRelationships(string part)
        {
            string folder = Path.GetDirectoryName(part).Replace('\\', '/');
            string relsPart = (folder.Length == 0 ? "" : folder + "/") + "_rels/" + Path.GetFileName(part) + ".rels";
            XDocument rels = LoadXml(relsPart);
            var result = new Dictionary<string, RelationshipInfo>(StringComparer.OrdinalIgnoreCase);
            if (rels == null || rels.Root == null) return result;
            foreach (XElement relationship in rels.Root.Elements(PKGREL + "Relationship"))
            {
                string id = Attr(relationship, "Id");
                if (string.IsNullOrWhiteSpace(id)) continue;
                result[id] = new RelationshipInfo
                {
                    Target = Attr(relationship, "Target") ?? "",
                    External = string.Equals(Attr(relationship, "TargetMode"), "External", StringComparison.OrdinalIgnoreCase)
                };
            }
            return result;
        }

        private XDocument LoadXml(string part)
        {
            ZipArchiveEntry entry = GetEntry(part);
            if (entry == null) return null;
            using (Stream stream = entry.Open()) return XDocument.Load(stream, LoadOptions.PreserveWhitespace);
        }

        private ZipArchiveEntry GetEntry(string part)
        {
            string normalized = NormalizePart(part);
            return package.Entries.FirstOrDefault(entry => string.Equals(NormalizePart(entry.FullName), normalized, StringComparison.OrdinalIgnoreCase));
        }

        private static string ResolvePart(string sourcePart, string target)
        {
            if (target.StartsWith("/", StringComparison.Ordinal)) return NormalizePart(target);
            string folder = Path.GetDirectoryName(sourcePart).Replace('\\', '/');
            var parts = new List<string>();
            foreach (string part in (folder + "/" + target).Split('/'))
            {
                if (part.Length == 0 || part == ".") continue;
                if (part == "..") { if (parts.Count > 0) parts.RemoveAt(parts.Count - 1); }
                else parts.Add(part);
            }
            return string.Join("/", parts.ToArray());
        }

        private static string NormalizePart(string part)
        {
            return (part ?? "").Replace('\\', '/').TrimStart('/');
        }

        private static string MimeType(string part)
        {
            switch (Path.GetExtension(part).ToLowerInvariant())
            {
                case ".png": return "image/png";
                case ".gif": return "image/gif";
                case ".svg": return "image/svg+xml";
                case ".bmp": return "image/bmp";
                case ".tif": case ".tiff": return "image/tiff";
                case ".emf": return "image/emf";
                case ".wmf": return "image/wmf";
                default: return "image/jpeg";
            }
        }

        internal static string ParagraphPropertiesCss(XElement properties)
        {
            if (properties == null) return "";
            var fake = new StringBuilder();
            string alignment = Attr(properties.Element(W + "jc"), W + "val");
            if (alignment == "both" || alignment == "distribute") alignment = "justify";
            if (!string.IsNullOrWhiteSpace(alignment)) fake.Append("text-align:").Append(alignment).Append(";");
            return fake.ToString();
        }

        internal static string RunPropertiesCss(XElement properties)
        {
            if (properties == null) return "";
            var css = new StringBuilder();
            if (IsOn(properties.Element(W + "b"))) css.Append("font-weight:bold;");
            if (IsOn(properties.Element(W + "i"))) css.Append("font-style:italic;");
            string size = Attr(properties.Element(W + "sz"), W + "val");
            double sizeValue;
            if (double.TryParse(size, NumberStyles.Any, CultureInfo.InvariantCulture, out sizeValue)) css.Append("font-size:").Append(F(sizeValue / 2.0)).Append("pt;");
            string color = Attr(properties.Element(W + "color"), W + "val");
            if (IsHexColor(color)) css.Append("color:#").Append(color).Append(";");
            return css.ToString();
        }

        private static string StyleAttribute(string css)
        {
            return string.IsNullOrWhiteSpace(css) ? "" : " style=\"" + Html(css) + "\"";
        }

        private static string Html(string value)
        {
            if (string.IsNullOrEmpty(value)) return "";
            return value.Replace("&", "&amp;").Replace("<", "&lt;").Replace(">", "&gt;").Replace("\"", "&quot;").Replace("'", "&#39;");
        }

        private static string RemoveTags(string value)
        {
            if (string.IsNullOrEmpty(value)) return "";
            var result = new StringBuilder();
            bool inside = false;
            foreach (char c in value)
            {
                if (c == '<') inside = true;
                else if (c == '>') inside = false;
                else if (!inside && !char.IsWhiteSpace(c)) result.Append(c);
            }
            return result.ToString().Replace("&nbsp;", "");
        }

        internal static string Attr(XElement element, XName name)
        {
            if (element == null) return null;
            XAttribute attribute = element.Attribute(name);
            return attribute == null ? null : attribute.Value;
        }

        private static bool IsOn(XElement element)
        {
            if (element == null) return false;
            string value = Attr(element, W + "val");
            return value == null || (value != "0" && value != "false" && value != "off" && value != "none");
        }

        private static bool IsHexColor(string value)
        {
            if (string.IsNullOrWhiteSpace(value) || value.Length != 6 || value == "auto") return false;
            return value.All(Uri.IsHexDigit);
        }

        private static string NamedColor(string name)
        {
            switch ((name ?? "").ToLowerInvariant())
            {
                case "yellow": return "#ffff00";
                case "green": return "#00ff00";
                case "cyan": return "#00ffff";
                case "magenta": return "#ff00ff";
                case "blue": return "#0000ff";
                case "red": return "#ff0000";
                case "darkblue": return "#000080";
                case "darkcyan": return "#008080";
                case "darkgreen": return "#008000";
                case "darkmagenta": return "#800080";
                case "darkred": return "#800000";
                case "darkyellow": return "#808000";
                case "darkgray": return "#808080";
                case "lightgray": return "#c0c0c0";
                case "black": return "#000000";
                default: return "";
            }
        }

        private static void AddTwips(StringBuilder css, string property, string value)
        {
            double number;
            if (double.TryParse(value, NumberStyles.Any, CultureInfo.InvariantCulture, out number))
                css.Append(property).Append(":").Append(F(number / 20.0)).Append("pt;");
        }

        private static string Twips(string value)
        {
            double number;
            return double.TryParse(value, NumberStyles.Any, CultureInfo.InvariantCulture, out number) ? F(number / 20.0) : "0";
        }

        internal static string F(double value)
        {
            return value.ToString("0.###", CultureInfo.InvariantCulture);
        }

        internal static string CssFont(string value)
        {
            string font = string.IsNullOrWhiteSpace(value) ? "Calibri" : value.Replace("'", "");
            return "'" + font + "','Segoe UI',Arial,sans-serif";
        }

        private sealed class RelationshipInfo
        {
            internal string Target;
            internal bool External;
        }
    }

    internal sealed class StyleCatalog
    {
        private static readonly XNamespace W = "http://schemas.openxmlformats.org/wordprocessingml/2006/main";
        private readonly Dictionary<string, XElement> styleById = new Dictionary<string, XElement>(StringComparer.OrdinalIgnoreCase);
        internal string DefaultFont { get; private set; }
        internal double DefaultFontSize { get; private set; }

        internal StyleCatalog(XDocument styles)
        {
            DefaultFont = "Calibri";
            DefaultFontSize = 11;
            if (styles == null || styles.Root == null) return;
            XElement defaultRun = styles.Root.Element(W + "docDefaults");
            if (defaultRun != null)
            {
                XElement runProperties = defaultRun.Descendants(W + "rPrDefault").Elements(W + "rPr").FirstOrDefault();
                XElement fonts = runProperties == null ? null : runProperties.Element(W + "rFonts");
                string font = fonts == null ? null : (DocxHtmlRenderer.Attr(fonts, W + "ascii") ?? DocxHtmlRenderer.Attr(fonts, W + "hAnsi"));
                if (!string.IsNullOrWhiteSpace(font)) DefaultFont = font;
                double size;
                if (double.TryParse(DocxHtmlRenderer.Attr(runProperties == null ? null : runProperties.Element(W + "sz"), W + "val"), NumberStyles.Any, CultureInfo.InvariantCulture, out size))
                    DefaultFontSize = size / 2.0;
            }
            foreach (XElement style in styles.Root.Elements(W + "style"))
            {
                string id = DocxHtmlRenderer.Attr(style, W + "styleId");
                if (!string.IsNullOrWhiteSpace(id)) styleById[id] = style;
            }
        }

        internal string GetParagraphTag(string styleId)
        {
            string name = GetName(styleId).ToLowerInvariant().Replace(" ", "");
            if (name == "title" || name == "titre") return "h1";
            for (int level = 1; level <= 6; level++)
                if (name == "heading" + level || name == "titre" + level || name == "heading" + level.ToString(CultureInfo.InvariantCulture)) return "h" + level;
            return "p";
        }

        internal string GetParagraphCss(string styleId)
        {
            return GetStyleCss(styleId, true, new HashSet<string>(StringComparer.OrdinalIgnoreCase));
        }

        internal string GetRunCss(string styleId)
        {
            return GetStyleCss(styleId, false, new HashSet<string>(StringComparer.OrdinalIgnoreCase));
        }

        private string GetStyleCss(string styleId, bool paragraph, HashSet<string> visited)
        {
            if (string.IsNullOrWhiteSpace(styleId) || !visited.Add(styleId)) return "";
            XElement style;
            if (!styleById.TryGetValue(styleId, out style)) return "";
            string basedOn = DocxHtmlRenderer.Attr(style.Element(W + "basedOn"), W + "val");
            string css = GetStyleCss(basedOn, paragraph, visited);
            if (paragraph) css += DocxHtmlRenderer.ParagraphPropertiesCss(style.Element(W + "pPr"));
            css += DocxHtmlRenderer.RunPropertiesCss(style.Element(W + "rPr"));
            return css;
        }

        private string GetName(string styleId)
        {
            XElement style;
            if (string.IsNullOrWhiteSpace(styleId) || !styleById.TryGetValue(styleId, out style)) return styleId ?? "";
            return DocxHtmlRenderer.Attr(style.Element(W + "name"), W + "val") ?? styleId;
        }
    }

    internal sealed class NumberingCatalog
    {
        private static readonly XNamespace W = "http://schemas.openxmlformats.org/wordprocessingml/2006/main";
        private readonly Dictionary<string, string> abstractByNum = new Dictionary<string, string>();
        private readonly Dictionary<string, Dictionary<int, LevelInfo>> levelsByAbstract = new Dictionary<string, Dictionary<int, LevelInfo>>();
        private readonly Dictionary<string, int[]> countersByNum = new Dictionary<string, int[]>();

        internal NumberingCatalog(XDocument numbering)
        {
            if (numbering == null || numbering.Root == null) return;
            foreach (XElement abstractNumber in numbering.Root.Elements(W + "abstractNum"))
            {
                string id = DocxHtmlRenderer.Attr(abstractNumber, W + "abstractNumId");
                var levels = new Dictionary<int, LevelInfo>();
                foreach (XElement level in abstractNumber.Elements(W + "lvl"))
                {
                    int index;
                    if (!int.TryParse(DocxHtmlRenderer.Attr(level, W + "ilvl"), out index)) continue;
                    int start;
                    if (!int.TryParse(DocxHtmlRenderer.Attr(level.Element(W + "start"), W + "val"), out start)) start = 1;
                    levels[index] = new LevelInfo
                    {
                        Start = start,
                        Format = DocxHtmlRenderer.Attr(level.Element(W + "numFmt"), W + "val") ?? "decimal",
                        Text = DocxHtmlRenderer.Attr(level.Element(W + "lvlText"), W + "val") ?? ("%" + (index + 1))
                    };
                }
                levelsByAbstract[id] = levels;
            }
            foreach (XElement number in numbering.Root.Elements(W + "num"))
            {
                string id = DocxHtmlRenderer.Attr(number, W + "numId");
                string abstractId = DocxHtmlRenderer.Attr(number.Element(W + "abstractNumId"), W + "val");
                if (!string.IsNullOrWhiteSpace(id) && !string.IsNullOrWhiteSpace(abstractId)) abstractByNum[id] = abstractId;
            }
        }

        internal string NextMarker(XElement paragraphProperties)
        {
            XElement numProperties = paragraphProperties == null ? null : paragraphProperties.Element(W + "numPr");
            if (numProperties == null) return "";
            string numId = DocxHtmlRenderer.Attr(numProperties.Element(W + "numId"), W + "val");
            int level;
            if (!int.TryParse(DocxHtmlRenderer.Attr(numProperties.Element(W + "ilvl"), W + "val"), out level)) level = 0;
            level = Math.Max(0, Math.Min(8, level));
            string abstractId;
            Dictionary<int, LevelInfo> levels;
            if (!abstractByNum.TryGetValue(numId ?? "", out abstractId) || !levelsByAbstract.TryGetValue(abstractId, out levels)) return "•";
            LevelInfo current;
            if (!levels.TryGetValue(level, out current)) return "•";
            if (current.Format == "bullet") return string.IsNullOrWhiteSpace(current.Text) ? "•" : current.Text;

            int[] counters;
            if (!countersByNum.TryGetValue(numId, out counters))
            {
                counters = new int[9];
                countersByNum[numId] = counters;
            }
            if (counters[level] == 0) counters[level] = current.Start;
            else counters[level]++;
            for (int deeper = level + 1; deeper < counters.Length; deeper++) counters[deeper] = 0;

            string marker = current.Text;
            for (int index = 0; index <= level; index++)
            {
                LevelInfo info;
                if (!levels.TryGetValue(index, out info)) info = current;
                int value = counters[index] == 0 ? info.Start : counters[index];
                marker = marker.Replace("%" + (index + 1), FormatNumber(value, info.Format));
            }
            return marker;
        }

        private static string FormatNumber(int value, string format)
        {
            if (format == "lowerLetter" || format == "upperLetter")
            {
                int number = Math.Max(1, value);
                var text = new StringBuilder();
                while (number > 0) { number--; text.Insert(0, (char)('a' + number % 26)); number /= 26; }
                return format == "upperLetter" ? text.ToString().ToUpperInvariant() : text.ToString();
            }
            if (format == "lowerRoman" || format == "upperRoman")
            {
                string roman = ToRoman(value);
                return format == "lowerRoman" ? roman.ToLowerInvariant() : roman;
            }
            return value.ToString(CultureInfo.InvariantCulture);
        }

        private static string ToRoman(int value)
        {
            int number = Math.Max(1, Math.Min(3999, value));
            var result = new StringBuilder();
            int[] values = { 1000, 900, 500, 400, 100, 90, 50, 40, 10, 9, 5, 4, 1 };
            string[] symbols = { "M", "CM", "D", "CD", "C", "XC", "L", "XL", "X", "IX", "V", "IV", "I" };
            for (int i = 0; i < values.Length; i++) while (number >= values[i]) { result.Append(symbols[i]); number -= values[i]; }
            return result.ToString();
        }

        private sealed class LevelInfo
        {
            internal int Start;
            internal string Format;
            internal string Text;
        }
    }

    internal sealed class PageSetup
    {
        private static readonly XNamespace W = "http://schemas.openxmlformats.org/wordprocessingml/2006/main";
        internal double WidthMm = 210;
        internal double HeightMm = 297;
        internal double MarginTopMm = 20;
        internal double MarginRightMm = 20;
        internal double MarginBottomMm = 20;
        internal double MarginLeftMm = 20;
        internal double HeaderDistanceMm = 12.5;
        internal double FooterDistanceMm = 12.5;

        internal static PageSetup FromSection(XElement section)
        {
            var result = new PageSetup();
            if (section == null) return result;
            XElement size = section.Element(W + "pgSz");
            result.WidthMm = Mm(DocxHtmlRenderer.Attr(size, W + "w"), result.WidthMm);
            result.HeightMm = Mm(DocxHtmlRenderer.Attr(size, W + "h"), result.HeightMm);
            if (string.Equals(DocxHtmlRenderer.Attr(size, W + "orient"), "landscape", StringComparison.OrdinalIgnoreCase) && result.WidthMm < result.HeightMm)
            {
                double temp = result.WidthMm; result.WidthMm = result.HeightMm; result.HeightMm = temp;
            }
            XElement margin = section.Element(W + "pgMar");
            result.MarginTopMm = Mm(DocxHtmlRenderer.Attr(margin, W + "top"), result.MarginTopMm);
            result.MarginRightMm = Mm(DocxHtmlRenderer.Attr(margin, W + "right"), result.MarginRightMm);
            result.MarginBottomMm = Mm(DocxHtmlRenderer.Attr(margin, W + "bottom"), result.MarginBottomMm);
            result.MarginLeftMm = Mm(DocxHtmlRenderer.Attr(margin, W + "left"), result.MarginLeftMm);
            result.HeaderDistanceMm = Mm(DocxHtmlRenderer.Attr(margin, W + "header"), result.HeaderDistanceMm);
            result.FooterDistanceMm = Mm(DocxHtmlRenderer.Attr(margin, W + "footer"), result.FooterDistanceMm);
            return result;
        }

        private static double Mm(string twips, double fallback)
        {
            double value;
            return double.TryParse(twips, NumberStyles.Any, CultureInfo.InvariantCulture, out value) ? value * 25.4 / 1440.0 : fallback;
        }
    }
}
