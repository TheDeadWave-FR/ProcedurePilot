using System;
using System.Globalization;

namespace ProcedurePilot
{
    internal static class UiText
    {
        internal const string French = "fr";
        internal const string English = "en";

        private static string language = French;

        internal static string Language
        {
            get { return language; }
        }

        internal static bool IsEnglish
        {
            get { return string.Equals(language, English, StringComparison.Ordinal); }
        }

        internal static CultureInfo Culture
        {
            get { return CultureInfo.GetCultureInfo(IsEnglish ? "en-US" : "fr-FR"); }
        }

        internal static void SetLanguage(string value)
        {
            language = NormalizeLanguage(value);
        }

        internal static string NormalizeLanguage(string value)
        {
            return !string.IsNullOrWhiteSpace(value)
                && value.Trim().StartsWith(English, StringComparison.OrdinalIgnoreCase)
                ? English
                : French;
        }

        internal static string Get(string french, string english)
        {
            return IsEnglish ? english : french;
        }

        internal static string Format(string frenchFormat, string englishFormat, params object[] args)
        {
            return string.Format(Culture, Get(frenchFormat, englishFormat), args);
        }

        internal static string Count(int count, string frenchSingular, string frenchPlural, string englishSingular, string englishPlural)
        {
            string format = IsEnglish
                ? (count == 1 ? englishSingular : englishPlural)
                : (count == 1 ? frenchSingular : frenchPlural);
            return string.Format(Culture, format, count);
        }
    }
}
