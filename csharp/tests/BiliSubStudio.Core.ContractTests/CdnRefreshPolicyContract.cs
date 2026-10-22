using System.Net;
using BiliSubStudio.Core.Video;

namespace BiliSubStudio.Core.ContractTests;

// Field 2026-10-22: Bilibili playurl signatures die at ~2h. The old path
// treated the resulting 403 as a dead CDN route, rotated four endpoints that
// shared the expired signature, then pinned the job to one connection for 68
// minutes. These tests pin the classifier + proactive TTL so that regression
// cannot silently return.
internal static class CdnRefreshPolicyContract
{
    public static Task RunAsync()
    {
        var now = DateTimeOffset.UtcNow;
        True(!CdnRefreshPolicy.ShouldRefresh(default, now), "unsigned legacy streams must not trigger a proactive refresh");
        True(!CdnRefreshPolicy.ShouldRefresh(now - TimeSpan.FromMinutes(99), now), "fresh signatures must not refresh early");
        True(CdnRefreshPolicy.ShouldRefresh(now - CdnRefreshPolicy.SignatureRefreshAge, now), "signatures at the TTL must refresh proactively");
        True(CdnRefreshPolicy.ShouldRefresh(now - TimeSpan.FromMinutes(130), now), "overdue signatures must refresh proactively");
        Equal(100, CdnRefreshPolicy.DefaultSignatureRefreshMinutes);
        Equal("BILISUB_PLAYURL_TTL_MINUTES", CdnRefreshPolicy.RefreshMinutesEnvironmentVariable);

        True(CdnRefreshPolicy.IsSignatureExpiry(new HttpRequestException("CDN 403", null, HttpStatusCode.Forbidden)), "HTTP 403 must be classified as signature expiry");
        True(CdnRefreshPolicy.IsSignatureExpiry(new HttpRequestException("CDN 401", null, HttpStatusCode.Unauthorized)), "HTTP 401 must be classified as signature expiry");
        True(CdnRefreshPolicy.IsSignatureExpiry(new IOException("segment failed", new HttpRequestException("inner 403", null, HttpStatusCode.Forbidden))), "wrapped 403 must survive the segment IOException");
        True(!CdnRefreshPolicy.IsSignatureExpiry(new HttpRequestException("CDN 503", null, HttpStatusCode.ServiceUnavailable)), "5xx must stay a transport failure");
        True(!CdnRefreshPolicy.IsSignatureExpiry(new HttpRequestException("CDN 404", null, HttpStatusCode.NotFound)), "404 must stay a transport failure");
        True(!CdnRefreshPolicy.IsSignatureExpiry(new HttpRequestException("connection reset")), "status-less transport error must stay transport");
        True(!CdnRefreshPolicy.IsSignatureExpiry(new InvalidOperationException("not a CDN error")), "non-HTTP errors must not re-sign");
        True(!CdnRefreshPolicy.IsSignatureExpiry(null), "null error must not re-sign");
        return Task.CompletedTask;
    }

    private static void Equal<T>(T expected, T actual)
    {
        if (!EqualityComparer<T>.Default.Equals(expected, actual))
            throw new InvalidOperationException($"expected {expected}, got {actual}");
    }

    private static void True(bool condition, string message)
    {
        if (!condition) throw new InvalidOperationException(message);
    }
}
