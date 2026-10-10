using System.Net.Http;

namespace SignalScheduler.Shared;

/// <summary>
/// The primary update lookup reads a repository-controlled raw file instead
/// of GitHub REST APIs. The official Actions run URL is derived from validated
/// metadata, not an arbitrary network-provided URL.
/// </summary>
public static class UpdateFeedClient
{
    public const string RawUrl=
        "https://raw.githubusercontent.com/mosen6266-netizen/MS002/ui2-updates/latest.json";

    public static async Task<VerifiedUpdateManifest> FetchAsync(
        HttpClient client,CancellationToken ct)
    {
        using var reply=await client.GetAsync(RawUrl,ct);
        reply.EnsureSuccessStatusCode();
        if(reply.Content.Headers.ContentLength is long size && size>16384)
            throw new InvalidDataException("更新清单大小异常");
        var contents=await reply.Content.ReadAsStringAsync(ct);
        return UpdateManifestCodec.Parse(contents) ??
               throw new InvalidDataException("静态更新清单格式不正确");
    }
}
