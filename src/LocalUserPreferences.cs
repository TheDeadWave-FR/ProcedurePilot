using System;
using System.IO;
using System.Xml.Linq;

namespace ProcedurePilot
{
    internal static class LocalUserPreferences
    {
        private const string FolderName = "ProcedurePilot";
        private const string FileName = "UserSettings.xml";

        internal static string LoadLanguage(string fallback)
        {
            string normalizedFallback = UiText.NormalizeLanguage(fallback);
            try
            {
                string path = GetPath();
                if (!File.Exists(path)) return normalizedFallback;
                XElement root = XDocument.Load(path).Root;
                XElement language = root == null ? null : root.Element("Language");
                return language == null ? normalizedFallback : UiText.NormalizeLanguage(language.Value);
            }
            catch
            {
                return normalizedFallback;
            }
        }

        internal static void SaveLanguage(string language)
        {
            string path = GetPath();
            string folder = Path.GetDirectoryName(path);
            Directory.CreateDirectory(folder);
            var document = new XDocument(
                new XElement("UserSettings",
                    new XAttribute("version", "1"),
                    new XElement("Language", UiText.NormalizeLanguage(language))));
            string temporaryPath = path + ".tmp-" + Guid.NewGuid().ToString("N");
            document.Save(temporaryPath);
            try
            {
                if (File.Exists(path))
                {
                    try { File.Replace(temporaryPath, path, null, true); }
                    catch
                    {
                        File.Copy(temporaryPath, path, true);
                        File.Delete(temporaryPath);
                    }
                }
                else File.Move(temporaryPath, path);
            }
            finally
            {
                if (File.Exists(temporaryPath)) try { File.Delete(temporaryPath); } catch { }
            }
        }

        private static string GetPath()
        {
            string localData = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
            if (string.IsNullOrWhiteSpace(localData))
                throw new InvalidOperationException(UiText.Get(
                    "Le dossier local de l’utilisateur est introuvable.",
                    "The user's local data folder could not be found."));
            return Path.Combine(localData, FolderName, FileName);
        }
    }
}
