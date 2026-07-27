using System.Text.RegularExpressions;

namespace AngelEyeBmsBridge;

/// <summary>
/// Formats diagnostics without exposing credentials in console, journal, or SQLite audit text.
/// </summary>
public static class BridgeDiagnosticFormatter
{
    /// <summary>Header used to correlate one Worker request with BMS server logs.</summary>
    public const string CorrelationHeaderName = "X-Correlation-ID";

    private static readonly Regex AuthorizationPattern = new(
        @"(?i)(authorization\s*[:=]\s*bearer\s+)[^\s,;]+",
        RegexOptions.CultureInvariant);
    private static readonly Regex NamedSecretPattern = new(
        @"(?i)((?:client[_ -]?secret|signing[_ -]?key|password|jwt|token)\s*[:=]\s*)(?:""[^""]*""|'[^']*'|[^\s,;]+)",
        RegexOptions.CultureInvariant);
    private static readonly Regex JwtPattern = new(
        @"eyJ[A-Za-z0-9_-]+\.[A-Za-z0-9_-]+\.[A-Za-z0-9_-]+",
        RegexOptions.CultureInvariant);
    private static readonly Regex CorrelationIdPattern = new(
        "^[A-Za-z0-9][A-Za-z0-9:._-]{0,127}$",
        RegexOptions.CultureInvariant);

    /// <summary>Returns a one-line, bounded, credential-redacted diagnostic.</summary>
    public static string SanitizeForLog(string? text, int maxLength = 2000)
    {
        if (string.IsNullOrWhiteSpace(text))
        {
            return string.Empty;
        }

        string normalized = text.ReplaceLineEndings(" | ").Trim();
        normalized = AuthorizationPattern.Replace(normalized, "$1[REDACTED]");
        normalized = NamedSecretPattern.Replace(normalized, "$1[REDACTED]");
        normalized = JwtPattern.Replace(normalized, "[REDACTED]");
        return normalized.Length <= maxLength
            ? normalized
            : normalized[..maxLength] + "...";
    }

    /// <summary>
    /// Includes exception type and stack frames while applying the same secret redaction.
    /// </summary>
    public static string FormatException(Exception exception, int maxLength = 2000)
    {
        ArgumentNullException.ThrowIfNull(exception);
        return SanitizeForLog(exception.ToString(), maxLength);
    }

    /// <summary>Uses only a bounded safe correlation value in an outbound header.</summary>
    public static string NormalizeCorrelationId(string? candidate, string fallback)
    {
        string normalized = candidate?.Trim() ?? string.Empty;
        return CorrelationIdPattern.IsMatch(normalized)
            ? normalized
            : fallback;
    }
}
