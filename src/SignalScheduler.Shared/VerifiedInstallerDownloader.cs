using System.Net;
using System.Net.Http;
using System.Security.Cryptography;

namespace SignalScheduler.Shared;

/// <summary>
/// Official GitHub release installer download. A HEAD probe is not required:
/// many managed networks block HEAD but allow GET. All retries are bounded,
/// bytes go to a partial file, and the final file is never published unless
/// SHA-256 matches the independently verified CI manifest.
/// </summary>
public static class VerifiedInstallerDownloader
{
    const long MaxBytes=700L*1024*1024;
    public const int MaxAttempts=3;

    public static async Task DownloadAsync(HttpClient http,string url,
        string partialPath,string finalPath,string sha256,
        Action<double>? progress,CancellationToken ct)
    {
        if(!Uri.TryCreate(url,UriKind.Absolute,out var uri) ||
           uri.Scheme!=Uri.UriSchemeHttps ||
           uri.Host!="github.com" ||
           !uri.AbsolutePath.StartsWith("/mosen6266-netizen/MS002/releases/download/",
               StringComparison.OrdinalIgnoreCase) ||
           !url.EndsWith(".exe",StringComparison.OrdinalIgnoreCase))
            throw new ArgumentException("只允许 GitHub 官方发布的安装包 URL。");
        if(sha256.Length!=64 || !sha256.All(Uri.IsHexDigit))
            throw new ArgumentException("SHA256 清单格式不正确。");
        Exception? last=null;
        for(var attempt=1;attempt<=MaxAttempts;attempt++)
        {
            ct.ThrowIfCancellationRequested();
            try
            {
                if(File.Exists(partialPath))File.Delete(partialPath);
                using var response=await http.GetAsync(uri,
                    HttpCompletionOption.ResponseHeadersRead,ct);
                if(!response.IsSuccessStatusCode)
                {
                    // The browser fallback can handle SSO/proxy authentication.
                    if(response.StatusCode is HttpStatusCode.Forbidden or
                        HttpStatusCode.Unauthorized or HttpStatusCode.NotFound)
                        throw new InvalidDataException(
                            "GitHub 下载被网络或访问权限拒绝（HTTP "+
                            (int)response.StatusCode+"）。");
                    response.EnsureSuccessStatusCode();
                }
                if(response.Content.Headers.ContentLength is long reported &&
                   reported>MaxBytes)throw new InvalidDataException("下载文件超过允许大小。");
                await using(var input=await response.Content.ReadAsStreamAsync(ct))
                await using(var file=new FileStream(partialPath,FileMode.Create,
                    FileAccess.Write,FileShare.None,131072,true))
                {
                    var buffer=new byte[131072];
                    long total=0;
                    int n;
                    while((n=await input.ReadAsync(buffer,ct))>0)
                    {
                        total+=n;
                        if(total>MaxBytes)
                            throw new InvalidDataException("下载超过 700 MB 上限。");
                        await file.WriteAsync(buffer.AsMemory(0,n),ct);
                        if(response.Content.Headers.ContentLength is long size && size>0)
                            progress?.Invoke(Math.Clamp(total*100d/size,0,99.5));
                    }
                    if(response.Content.Headers.ContentLength is long length &&
                       total!=length)
                        throw new IOException("文件不完整，已自动安排重试。");
                }
                await using(var verify=File.OpenRead(partialPath))
                {
                    var actual=Convert.ToHexString(await SHA256.HashDataAsync(verify,ct));
                    if(!string.Equals(actual,sha256,StringComparison.OrdinalIgnoreCase))
                        throw new InvalidDataException("SHA256 不匹配，安装包未采用。");
                }
                File.Move(partialPath,finalPath,true);
                progress?.Invoke(100);
                return;
            }
            catch(OperationCanceledException) when(ct.IsCancellationRequested){throw;}
            catch(Exception ex) when(ex is HttpRequestException or IOException or
                InvalidDataException or TaskCanceledException)
            {
                last=ex;
                try{if(File.Exists(partialPath))File.Delete(partialPath);}catch(IOException){}
                if(attempt>=MaxAttempts || ex is InvalidDataException &&
                    ex.Message.Contains("GitHub 下载被网络"))break;
                await Task.Delay(TimeSpan.FromSeconds(attempt*2),ct);
            }
        }
        throw new IOException("3 次下载尝试未完成，请使用浏览器从 GitHub Releases 下载。",last);
    }
}
