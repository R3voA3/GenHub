using GenHub.Core.Constants;

namespace GenHub.Core.Helpers;

/// <summary>
/// Provides version comparison utilities for different version formats used by publishers.
/// </summary>
public static class VersionComparer
{
    /// <summary>
    /// Compares two version strings based on the publisher type.
    /// </summary>
    /// <param name="version1">The first version to compare.</param>
    /// <param name="version2">The second version to compare.</param>
    /// <param name="publisherType">The publisher type to determine version format.</param>
    /// <returns>
    /// Less than zero if version1 is less than version2.
    /// Zero if version1 equals version2.
    /// Greater than zero if version1 is greater than version2.
    /// </returns>
    public static int CompareVersions(string? version1, string? version2, string? publisherType)
    {
        // Handle null/empty cases
        if (string.IsNullOrWhiteSpace(version1) && string.IsNullOrWhiteSpace(version2))
            return 0;
        if (string.IsNullOrWhiteSpace(version1))
            return -1;
        if (string.IsNullOrWhiteSpace(version2))
            return 1;

        // Determine comparison strategy based on publisher type
        if (string.Equals(publisherType, CommunityOutpostConstants.PublisherType, StringComparison.OrdinalIgnoreCase))
        {
            // Community Outpost uses date-based versions (YYYY-MM-DD)
            return CompareDateVersions(version1, version2);
        }
        else if (string.Equals(publisherType, PublisherTypeConstants.TheSuperHackers, StringComparison.OrdinalIgnoreCase))
        {
            // TheSuperHackers uses numeric versions (e.g., "20251226")
            return CompareNumericVersions(version1, version2);
        }
        else if (string.Equals(publisherType, PublisherTypeConstants.GeneralsOnline, StringComparison.OrdinalIgnoreCase))
        {
            // GeneralsOnline uses numeric versions
            return CompareNumericVersions(version1, version2);
        }

        // Default: try numeric comparison, fall back to string comparison
        var numericResult = TryCompareNumericVersions(version1, version2);
        if (numericResult.HasValue)
            return numericResult.Value;

        // Fall back to ordinal string comparison
        return string.Compare(version1, version2, StringComparison.Ordinal);
    }

    /// <summary>
    /// Compares two date-based version strings in YYYY-MM-DD format.
    /// </summary>
    /// <param name="date1">The first date version.</param>
    /// <param name="date2">The second date version.</param>
    /// <returns>Comparison result.</returns>
    private static int CompareDateVersions(string date1, string date2)
    {
        // Try to parse as dates
        if (TryParseDateVersion(date1, out var parsedDate1) && TryParseDateVersion(date2, out var parsedDate2))
        {
            return parsedDate1.CompareTo(parsedDate2);
        }

        // If parsing fails, try to extract numeric representation (YYYYMMDD)
        var numeric1 = ExtractNumericFromDate(date1);
        var numeric2 = ExtractNumericFromDate(date2);

        if (numeric1.HasValue && numeric2.HasValue)
        {
            return numeric1.Value.CompareTo(numeric2.Value);
        }

        // Fall back to string comparison
        return string.Compare(date1, date2, StringComparison.Ordinal);
    }

    /// <summary>
    /// Compares two numeric or semantic version strings.
    /// </summary>
    /// <param name="ver1">The first version.</param>
    /// <param name="ver2">The second version.</param>
    /// <returns>Comparison result.</returns>
    private static int CompareNumericVersions(string ver1, string ver2)
    {
        // Try to parse as long integers for direct comparison (e.g. "20250101")
        bool isNum1 = long.TryParse(ver1, out var n1);
        bool isNum2 = long.TryParse(ver2, out var n2);

        if (isNum1 && isNum2)
        {
            return NormalizeNumericDate(n1).CompareTo(NormalizeNumericDate(n2));
        }

        // Handle mixed Semantic vs Numeric-Date case
        // If one is semantic (has dots) and the other is a "large" number (likely a date),
        // we assume the Semantic version is newer (treating date-versions as legacy/v0).
        bool hasDot1 = ver1.Contains('.');
        bool hasDot2 = ver2.Contains('.');

        if (hasDot1 && isNum2)
        {
             // ver1 is Semantic, ver2 is Numeric
             // If ver2 looks like a date (> 100000), treat ver1 as newer if it starts with >= 1
             if (n2 > 100000) return 1;
        }
        else if (isNum1 && hasDot2)
        {
             // ver1 is Numeric, ver2 is Semantic
             if (n1 > 100000) return -1;
        }

        // Handle standard semantic versions (e.g. "1.10" vs "2.0")
        if (hasDot1 || hasDot2)
        {
             return CompareSemanticVersions(ver1, ver2);
        }

        // Try to extract digits and compare
        var digits1 = ExtractDigits(ver1);
        var digits2 = ExtractDigits(ver2);

        if (long.TryParse(digits1, out var ld1) && long.TryParse(digits2, out var ld2))
        {
            return NormalizeNumericDate(ld1).CompareTo(NormalizeNumericDate(ld2));
        }

        // Fall back to string comparison
        return string.Compare(ver1, ver2, StringComparison.Ordinal);
    }

    /// <summary>
    /// Normalizes a numeric version that might be YYMMDD to YYYYMMDD.
    /// Assumes 20xx for dates starting with 2-9 (e.g. 210101 -> 20210101).
    /// </summary>
    private static long NormalizeNumericDate(long version)
    {
        // 6 digits is likely YYMMDD (e.g. 260116)
        if (version >= 100000 && version <= 991231)
        {
            return 20000000 + version;
        }

        return version;
    }

    /// <summary>
    /// Compares two semantic version strings segment by segment.
    /// </summary>
    private static int CompareSemanticVersions(string ver1, string ver2)
    {
        var parts1 = ver1.Split('.', StringSplitOptions.RemoveEmptyEntries);
        var parts2 = ver2.Split('.', StringSplitOptions.RemoveEmptyEntries);

        for (int i = 0; i < Math.Max(parts1.Length, parts2.Length); i++)
        {
            var segment1 = i < parts1.Length ? ExtractDigits(parts1[i]) : "0";
            var segment2 = i < parts2.Length ? ExtractDigits(parts2[i]) : "0";

            if (long.TryParse(segment1, out var num1) && long.TryParse(segment2, out var num2))
            {
                if (num1 != num2) return num1.CompareTo(num2);
            }
            else
            {
                var strCompare = string.Compare(segment1, segment2, StringComparison.OrdinalIgnoreCase);
                if (strCompare != 0) return strCompare;
            }
        }

        return 0;
    }

    /// <summary>
    /// Tries to compare two versions as numeric values.
    /// </summary>
    /// <returns>Comparison result if successful, null otherwise.</returns>
    private static int? TryCompareNumericVersions(string ver1, string ver2)
    {
        if (long.TryParse(ver1, out var num1) && long.TryParse(ver2, out var num2))
        {
            return num1.CompareTo(num2);
        }

        return null;
    }

    /// <summary>
    /// Tries to parse a date version string in YYYY-MM-DD format.
    /// </summary>
    private static bool TryParseDateVersion(string dateStr, out DateTime result)
    {
        // Try exact format YYYY-MM-DD first with InvariantCulture
        if (DateTime.TryParseExact(dateStr, "yyyy-MM-dd", System.Globalization.CultureInfo.InvariantCulture, System.Globalization.DateTimeStyles.None, out result))
        {
            return true;
        }

        // Fallback to standard date parsing with InvariantCulture
        if (DateTime.TryParse(dateStr, System.Globalization.CultureInfo.InvariantCulture, System.Globalization.DateTimeStyles.None, out result))
        {
            return true;
        }

        result = default;
        return false;
    }

    /// <summary>
    /// Extracts numeric representation from a date string (YYYYMMDD).
    /// </summary>
    private static long? ExtractNumericFromDate(string dateStr)
    {
        if (string.IsNullOrWhiteSpace(dateStr))
            return null;

        // Remove common date separators
        var digits = dateStr.Replace("-", string.Empty)
                           .Replace("/", string.Empty)
                           .Replace(".", string.Empty);

        if (long.TryParse(digits, out var result))
        {
            return result;
        }

        return null;
    }

    /// <summary>
    /// Extracts all digits from a version string.
    /// </summary>
    private static string ExtractDigits(string version)
    {
        if (string.IsNullOrWhiteSpace(version))
            return string.Empty;

        var result = new System.Text.StringBuilder();
        foreach (var c in version)
        {
            if (char.IsDigit(c))
            {
                result.Append(c);
            }
        }

        return result.ToString();
    }
}
