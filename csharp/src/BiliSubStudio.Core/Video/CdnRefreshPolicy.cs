using System.Net;

namespace BiliSubStudio.Core.Video;

public enum RefreshCause
{
    TransportFailure,
    SignatureExpired,
    LowThroughput,
}

// Bilibili playurl URLs carry a short-lived signature (field data: HTTP 403
// starts exactly two hours after resolve). Refresh proactively before that
// deadline instead of rotating four endpoints that all share the same expired
// signature, and keep the configured concurrency once the new signature lands.
public static class CdnRefreshPolicy
{
    public const int DefaultSignatureRefreshMinutes = 100;
    public const string RefreshMinutesEnvironmentVariable = "BILISUB_PLAYURL_TTL_MINUTES";
    public static readonly TimeSpan DefaultSignatureRefreshAge = TimeSpan.FromMinutes(DefaultSignatureRefreshMinutes);

    public static TimeSpan SignatureRefreshAge
    {
        get
        {
            var raw = Environment.GetEnvironmentVariable(RefreshMinutesEnvironmentVariable);
            return int.TryParse(raw, out var minutes) && minutes is >= 1 and <= 720
                ? TimeSpan.FromMinutes(minutes)
                : DefaultSignatureRefreshAge;
        }
    }

    public static bool ShouldRefresh(DateTimeOffset signedAtUtc, DateTimeOffset now) =>
        signedAtUtc != default && now - signedAtUtc >= SignatureRefreshAge;

    public static bool IsSignatureExpiry(Exception? error)
    {
        for (var current = error; current is not null; current = current.InnerException)
        {
            if (current is HttpRequestException http
                && http.StatusCode is HttpStatusCode.Unauthorized or HttpStatusCode.Forbidden)
            {
                return true;
            }
        }
        return false;
    }
}
