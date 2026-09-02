using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Xml.Linq;

namespace ProcedurePilot
{
    internal static class ProcedureXmlIndex
    {
        internal static int Write(string indexPath, string rootPath, string documentsPath, string pdfPath)
        {
            string fullIndexPath = Path.GetFullPath(string.IsNullOrWhiteSpace(indexPath)
                ? Path.Combine(rootPath, "Data", "Index.xml")
                : indexPath);
            string indexFolder = Path.GetDirectoryName(fullIndexPath);
            if (string.IsNullOrWhiteSpace(indexFolder))
                throw new InvalidOperationException(UiText.Get("Le dossier de l’index XML est invalide.", "The XML index folder is invalid."));
            Directory.CreateDirectory(indexFolder);

            var byName = new Dictionary<string, IndexEntry>(StringComparer.OrdinalIgnoreCase);
            foreach (string path in DocumentSupport.GetDocumentFiles(documentsPath))
            {
                string key = Path.GetFileNameWithoutExtension(path);
                IndexEntry entry = GetOrCreate(byName, key);
                if (entry.ModifiablePath == null || DocumentSupport.GetPreference(path) < DocumentSupport.GetPreference(entry.ModifiablePath))
                    entry.ModifiablePath = Path.GetFullPath(path);
            }

            if (Directory.Exists(pdfPath))
            {
                foreach (string path in Directory.GetFiles(pdfPath, "*.pdf"))
                {
                    string key = Path.GetFileNameWithoutExtension(path);
                    IndexEntry entry;
                    if (!byName.TryGetValue(key, out entry)) continue;
                    if (entry.PdfPath == null) entry.PdfPath = Path.GetFullPath(path);
                }
            }

            IndexEntry[] entries = byName.Values
                .OrderBy(entry => entry.Name, StringComparer.CurrentCultureIgnoreCase)
                .ToArray();
            ProcedureTag.EnsureUniqueCodes(entries.Select(entry => entry.Name));
            var root = new XElement("DocumentIndex",
                new XAttribute("version", "1"),
                new XAttribute("generatedAtUtc", DateTime.UtcNow.ToString("o")),
                new XAttribute("rootPath", Path.GetFullPath(rootPath)),
                new XAttribute("count", entries.Length));

            foreach (IndexEntry entry in entries)
            {
                string code = ProcedureTag.GetCode(entry.Name);
                var documentEntry = new XElement("Document",
                    new XAttribute("name", entry.Name),
                    new XAttribute("status", GetState(entry)));
                if (code.Length > 0) documentEntry.Add(new XAttribute("code", code));
                if (entry.ModifiablePath != null) documentEntry.Add(CreateFileElement("EditableFile", entry.ModifiablePath, indexFolder));
                if (entry.PdfPath != null) documentEntry.Add(CreateFileElement("PdfFile", entry.PdfPath, indexFolder));
                root.Add(documentEntry);
            }

            var document = new XDocument(
                new XDeclaration("1.0", "utf-8", "yes"),
                new XComment("Local document index generated automatically by Procedure Pilot."),
                root);
            string tempPath = Path.Combine(indexFolder, ".procedurepilot-index-" + Guid.NewGuid().ToString("N") + ".tmp");
            document.Save(tempPath);
            try
            {
                if (File.Exists(fullIndexPath))
                {
                    try { File.Replace(tempPath, fullIndexPath, null, true); }
                    catch
                    {
                        File.Copy(tempPath, fullIndexPath, true);
                        File.Delete(tempPath);
                    }
                }
                else File.Move(tempPath, fullIndexPath);
            }
            finally
            {
                if (File.Exists(tempPath)) try { File.Delete(tempPath); } catch { }
            }
            return entries.Length;
        }

        private static IndexEntry GetOrCreate(Dictionary<string, IndexEntry> entries, string name)
        {
            IndexEntry entry;
            if (!entries.TryGetValue(name, out entry))
            {
                entry = new IndexEntry { Name = name };
                entries.Add(name, entry);
            }
            return entry;
        }

        private static XElement CreateFileElement(string elementName, string path, string indexFolder)
        {
            var info = new FileInfo(path);
            return new XElement(elementName,
                new XAttribute("name", info.Name),
                new XAttribute("relativePath", MakeRelativePath(indexFolder, info.FullName)),
                new XAttribute("uri", new Uri(info.FullName).AbsoluteUri),
                new XAttribute("format", info.Extension.TrimStart('.').ToLowerInvariant()),
                new XAttribute("sizeBytes", info.Length),
                new XAttribute("addedAtUtc", info.CreationTimeUtc.ToString("o")),
                new XAttribute("modifiedAtUtc", info.LastWriteTimeUtc.ToString("o")));
        }

        private static string MakeRelativePath(string baseFolder, string path)
        {
            try
            {
                string baseWithSeparator = Path.GetFullPath(baseFolder).TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar;
                Uri baseUri = new Uri(baseWithSeparator);
                Uri pathUri = new Uri(Path.GetFullPath(path));
                if (!string.Equals(baseUri.Scheme, pathUri.Scheme, StringComparison.OrdinalIgnoreCase)) return pathUri.AbsoluteUri;
                return Uri.UnescapeDataString(baseUri.MakeRelativeUri(pathUri).ToString()).Replace('/', Path.DirectorySeparatorChar);
            }
            catch { return Path.GetFullPath(path); }
        }

        private static string GetState(IndexEntry entry)
        {
            if (DocumentSupport.IsPdfCurrent(entry.ModifiablePath, entry.PdfPath)) return "synchronized";
            if (entry.ModifiablePath != null && entry.PdfPath != null) return "pdf-outdated";
            if (entry.ModifiablePath != null) return "pdf-missing";
            return "pdf-only";
        }

        private sealed class IndexEntry
        {
            internal string Name;
            internal string ModifiablePath;
            internal string PdfPath;
        }
    }
}
