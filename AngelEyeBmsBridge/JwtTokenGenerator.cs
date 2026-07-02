using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace AngelEyeBmsBridge;

/// <summary>
/// Generates HS256 JWT tokens that identify the ANGEL source provider when posting to BMS.
/// </summary>
public static class JwtTokenGenerator
{
    /// <summary>Claim type used by BMS as the source provider identifier.</summary>
    public const string NameIdentifierClaim = "http://schemas.xmlsoap.org/ws/2005/05/identity/claims/nameidentifier";

    /// <summary>Claim type used by BMS as the source provider token serial number.</summary>
    public const string SerialNumberClaim = "http://schemas.microsoft.com/ws/2008/06/identity/claims/serialnumber";

    /// <summary>
    /// Creates a signed source provider token using the bridge JWT settings.
    /// </summary>
    /// <param name="nameIdentifier">BMS SourceProvider GUID claim.</param>
    /// <param name="serialNumber">BMS SourceProviderToken serial number claim.</param>
    /// <param name="issuer">Expected JWT issuer.</param>
    /// <param name="audience">Expected JWT audience.</param>
    /// <param name="signingKey">Shared HS256 signing key.</param>
    /// <param name="lifetimeMinutes">Token lifetime in minutes.</param>
    /// <returns>A compact JWT string.</returns>
    public static string GenerateSourceProviderToken(
        string nameIdentifier,
        string serialNumber,
        string issuer,
        string audience,
        string signingKey,
        int lifetimeMinutes)
    {
        if (string.IsNullOrWhiteSpace(nameIdentifier))
        {
            throw new ArgumentException("請輸入訊源商 ID。", nameof(nameIdentifier));
        }

        if (string.IsNullOrWhiteSpace(serialNumber))
        {
            throw new ArgumentException("請輸入訊源商序號。", nameof(serialNumber));
        }

        if (string.IsNullOrWhiteSpace(issuer))
        {
            throw new ArgumentException("請輸入 Issuer。", nameof(issuer));
        }

        if (string.IsNullOrWhiteSpace(audience))
        {
            throw new ArgumentException("請輸入 Audience。", nameof(audience));
        }

        if (string.IsNullOrWhiteSpace(signingKey))
        {
            throw new ArgumentException("請輸入 Signing Key。", nameof(signingKey));
        }

        long exp = DateTimeOffset.UtcNow.AddMinutes(Math.Max(lifetimeMinutes, 1)).ToUnixTimeSeconds();
        Dictionary<string, object> header = new()
        {
            ["alg"] = "HS256",
            ["typ"] = "JWT"
        };
        Dictionary<string, object> payload = new()
        {
            [NameIdentifierClaim] = nameIdentifier.Trim(),
            [SerialNumberClaim] = serialNumber.Trim(),
            ["iss"] = issuer.Trim(),
            ["aud"] = audience.Trim(),
            ["exp"] = exp
        };

        string headerPart = Base64UrlEncode(JsonSerializer.SerializeToUtf8Bytes(header));
        string payloadPart = Base64UrlEncode(JsonSerializer.SerializeToUtf8Bytes(payload));
        string unsignedToken = $"{headerPart}.{payloadPart}";

        using HMACSHA256 hmac = new(Encoding.UTF8.GetBytes(signingKey));
        string signaturePart = Base64UrlEncode(hmac.ComputeHash(Encoding.ASCII.GetBytes(unsignedToken)));
        return $"{unsignedToken}.{signaturePart}";
    }

    private static string Base64UrlEncode(byte[] bytes)
    {
        return Convert.ToBase64String(bytes)
            .TrimEnd('=')
            .Replace('+', '-')
            .Replace('/', '_');
    }
}
