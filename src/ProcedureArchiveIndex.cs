using System;
using System.IO;
using System.Linq;
using System.Xml.Linq;

namespace ProcedurePilot
{
    internal static class ProcedureArchiveIndex
    {
        internal static int Write(string indexPath, string archivePath)
        {
            string fullIndexPath = Path.GetFullPath(indexPath);
            string indexFolder = Path.GetDirectoryName(fullIndexPath);
            if (string.IsNullOrWhiteSpace(indexFolder))
                throw new InvalidOperationException(UiText.Get("Le dossier de l’index des archives est invalide.", "The archive index folder is invalid."));
            Directory.CreateDirectory(indexFolder);

            string[] files = DocumentSupport.GetDocumentFiles(archivePath, true);
            var root = new XElement("ArchiveIndex",
                new XAttribute("version", "1"),
                new XAttribute("generatedAtUtc", DateTime.UtcNow.ToString("o")),
                new XAttribute("archivePath", Path.GetFullPath(archivePath)),
                new XAttribute("count", files.Length));

            foreach (string path in files.OrderBy(value => value, StringComparer.CurrentCultureIgnoreCase))
            {
                var info = new FileInfo(path);
                string name = Path.GetFileNameWithoutExtension(path);
                string code = ProcedureTag.GetCode(name);
                var document = new XElement("Document",
                    new XAttribute("name", name));
                if (code.Length > 0) document.Add(new XAttribute("code", code));
                document.Add(new XElement("EditableFile",
                    new XAttribute("name", info.Name),
                    new XAttribute("relativePath", MakeRelativePath(indexFolder, info.FullName)),
                    new XAttribute("archiveRelativePath", MakeRelativePath(archivePath, info.FullName)),
                    new XAttribute("uri", new Uri(info.FullName).AbsoluteUri),
                    new XAttribute("format", info.Extension.TrimStart('.').ToLowerInvariant()),
                    new XAttribute("sizeBytes", info.Length),
                    new XAttribute("addedAtUtc", info.CreationTimeUtc.ToString("o")),
                    new XAttribute("modifiedAtUtc", info.LastWriteTimeUtc.ToString("o"))));
                root.Add(document);
            }

            var xml = new XDocument(
                new XDeclaration("1.0", "utf-8", "yes"),
                new XComment("Archive index generated automatically by Procedure Pilot."),
                root);
            string tempPath = Path.Combine(indexFolder, ".procedurepilot-archive-" + Guid.NewGuid().ToString("N") + ".tmp");
            xml.Save(tempPath);
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
            return files.Length;
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
    }
}
