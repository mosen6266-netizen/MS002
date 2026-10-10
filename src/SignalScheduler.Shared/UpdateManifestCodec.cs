using System.Text.Json;
using System.Text.RegularExpressions;

namespace SignalScheduler.Shared;

/// <summary>
/// Public GitHub-hosted update metadata created only after Windows CI finishes
/// all build, installer, and upgrade smoke checks. Fetching the raw branch file
/// does not consume the unauthenticated GitHub REST API quota.
/// </summary>
public sealed record VerifiedUpdateManifest(
    string Version,int Build,long RunId,string SourceSha,string InstallerSha256)
{
    public string RunUrl=>$"https://github.com/mosen6266-netizen/MS002/actions/runs/{RunId}";
}

public static class UpdateManifestCodec
{
    static readonly Regex VersionPattern=new(
        @"^8\.\d{1,4}\.\d{1,4}-(?:alpha|beta|rc)\.\d{1,5}$",
        RegexOptions.CultureInvariant|RegexOptions.Compiled);
    static readonly Regex CommitPattern=new(
        "^[a-fA-F0-9]{40}$",RegexOptions.CultureInvariant|RegexOptions.Compiled);
    static readonly Regex HashPattern=new(
        "^[a-fA-F0-9]{64}$",RegexOptions.CultureInvariant|RegexOptions.Compiled);

    public static VerifiedUpdateManifest? Parse(string? json)
    {
        if(string.IsNullOrWhiteSpace(json)||json.Length>16384)return null;
        try
        {
            using var doc=JsonDocument.Parse(json);
            var root=doc.RootElement;
            if(root.ValueKind!=JsonValueKind.Object ||
               !root.TryGetProperty("version",out var v) ||
               v.ValueKind!=JsonValueKind.String ||
               !root.TryGetProperty("build",out var b) ||
               !b.TryGetInt32(out var build) || build<=0 ||
               !root.TryGetProperty("runId",out var r) ||
               !r.TryGetInt64(out var run) || run<=0 ||
               !root.TryGetProperty("sourceSha",out var sha) ||
               sha.ValueKind!=JsonValueKind.String ||
               !root.TryGetProperty("installerSha256",out var hash) ||
               hash.ValueKind!=JsonValueKind.String)return null;
            var version=v.GetString()??"";
            var source=sha.GetString()??"";
            var installer=hash.GetString()??"";
            if(!VersionPattern.IsMatch(version) ||
               !CommitPattern.IsMatch(source) ||
               !HashPattern.IsMatch(installer))return null;
            return new VerifiedUpdateManifest(version,build,run,source,installer);
        }
        catch(JsonException){return null;}
    }

    public static string Serialize(VerifiedUpdateManifest info)=>
        JsonSerializer.Serialize(new{
            version=info.Version,build=info.Build,runId=info.RunId,
            sourceSha=info.SourceSha,installerSha256=info.InstallerSha256
        });
}
