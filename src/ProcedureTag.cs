using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;

namespace ProcedurePilot
{
    internal static class ProcedureTag
    {
        internal const string DefaultPrefix = "FR";
        internal const int MaximumPrefixLength = 16;
        internal const int MaximumNumber = 99999;

        private static readonly Regex PrefixPattern = new Regex(
            "^[A-Z][A-Z0-9]*(?:-[A-Z0-9]+)*$",
            RegexOptions.CultureInvariant);

        private static readonly Regex CodePattern = new Regex(
            "^(?<code>(?<prefix>[A-Z][A-Z0-9]*(?:-[A-Z0-9]+)*)-(?<number>(?!00000)[0-9]{5}))(?=\\s|$)",
            RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);

        internal static bool TryNormalizePrefix(string value, out string normalized)
        {
            normalized = (value ?? string.Empty).Trim().ToUpperInvariant();
            return normalized.Length >= 1
                && normalized.Length <= MaximumPrefixLength
                && PrefixPattern.IsMatch(normalized);
        }

        internal static string NormalizePrefixOrDefault(string value)
        {
            string normalized;
            return TryNormalizePrefix(value, out normalized) ? normalized : DefaultPrefix;
        }

        internal static string ValidateAndNormalizePrefix(string value)
        {
            string normalized;
            if (TryNormalizePrefix(value, out normalized)) return normalized;
            throw new InvalidOperationException(UiText.Get(
                "Le préfixe du tag doit contenir de 1 à 16 caractères : lettres non accentuées, chiffres et tirets internes. Il doit commencer par une lettre.",
                "The tag prefix must contain 1 to 16 characters: unaccented letters, numbers, and internal hyphens. It must start with a letter."));
        }

        internal static string FormatCode(string prefix, int number)
        {
            if (number < 1 || number > MaximumNumber)
                throw new ArgumentOutOfRangeException("number");
            return ValidateAndNormalizePrefix(prefix) + "-" + number.ToString("00000", CultureInfo.InvariantCulture);
        }

        internal static string GetCode(string procedureName)
        {
            Match match = CodePattern.Match(procedureName ?? string.Empty);
            if (!match.Success) return string.Empty;
            string normalizedPrefix;
            if (!TryNormalizePrefix(match.Groups["prefix"].Value, out normalizedPrefix)) return string.Empty;
            return normalizedPrefix + "-" + match.Groups["number"].Value;
        }

        internal static bool HasCode(string procedureName)
        {
            return GetCode(procedureName).Length > 0;
        }

        internal static void EnsureUniqueCodes(IEnumerable<string> procedureNames)
        {
            if (procedureNames == null) return;
            string duplicate = procedureNames
                .Select(GetCode)
                .Where(code => code.Length > 0)
                .GroupBy(code => code, StringComparer.OrdinalIgnoreCase)
                .Where(group => group.Count() > 1)
                .Select(group => group.Key)
                .FirstOrDefault();
            if (string.IsNullOrWhiteSpace(duplicate)) return;

            throw new InvalidDataException(UiText.Get(
                "Le code « " + duplicate + " » est utilisé par plusieurs procédures. Renommez les fichiers concernés avant de continuer.",
                "The code '" + duplicate + "' is used by several procedures. Rename the affected files before continuing."));
        }

        internal static bool TryGetNumber(string procedureName, string prefix, out int number)
        {
            number = 0;
            string normalizedPrefix;
            if (!TryNormalizePrefix(prefix, out normalizedPrefix)) return false;

            Match match = CodePattern.Match(procedureName ?? string.Empty);
            return match.Success
                && string.Equals(match.Groups["prefix"].Value, normalizedPrefix, StringComparison.OrdinalIgnoreCase)
                && int.TryParse(match.Groups["number"].Value, NumberStyles.None, CultureInfo.InvariantCulture, out number);
        }
    }
}
