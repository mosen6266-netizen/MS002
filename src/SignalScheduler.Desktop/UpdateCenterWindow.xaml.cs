using System.Diagnostics;
using System.IO;
using System.Net.Http;
using System.Net;
using SignalScheduler.Shared;
using System.Security.Cryptography;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Windows;
using System.Windows.Controls;

namespace SignalScheduler.Desktop;

/// <summary>Read-only version discovery plus verified official release downloads.
/// CI artifacts require the user's GitHub session and open in their browser.</summary>
public partial class UpdateCenterWindow : UserControl
{
    const string Repo="mosen6266-netizen/MS002";
    const string CurrentProductVersion="8.0.0-beta.13";

    const string WorkflowUrl="https://github.com/mosen6266-netizen/MS002/actions/workflows/windows-build.yml";
    static readonly string Api="https://api.github.com/repos/"+Repo;
    readonly HttpClient _http=new(){Timeout=Timeout.InfiniteTimeSpan};
    bool _checking;
    bool _downloading;
    int _currentBuild;
    int _latestBuild;
    string? _latestRunUrl;
    string? _latestVersion;
    string? _expectedSha256;
    DateTimeOffset _nextApiRetryAt;
    string? _latestSha;
    string? _downloadUrl;
    string? _checksumUrl;
    string? _releaseName;
    string? _downloaded;

    public UpdateCenterWindow()
    {
        InitializeComponent();
        _http.DefaultRequestHeaders.UserAgent.ParseAdd("MS002-UpdateCenter/8.0");
        _http.DefaultRequestHeaders.Accept.ParseAdd("application/vnd.github+json");
        LoadInstalledBuild();
        var cached=TryLoadCache();
        if(cached is not null)
        {
            ApplyManifest(cached);
            UpdateStatusText.Text="已显示上次核实的构建缓存，尚未联网检查是否有更新。";
        }
        UpdateButtons();
        Unloaded+=(_,_)=>{ /* Keep downloads explicit, no background polling. */ };
    }

    void LoadInstalledBuild()
    {
        _currentBuild=0;
        try
        {
            var file=Path.Combine(AppContext.BaseDirectory,"build-info.json");
            if(File.Exists(file))
            {
                using var doc=JsonDocument.Parse(File.ReadAllText(file));
                if(doc.RootElement.TryGetProperty("build",out var value) &&
                   value.TryGetInt32(out var n) && n>0)
                    _currentBuild=n;
            }
        }
        catch(JsonException) { }
        catch(IOException) { }
        catch(UnauthorizedAccessException) { }
        CurrentVersionText.Text="V"+CurrentProductVersion+
            (_currentBuild>0?$"  ·  Build #{_currentBuild}":"");
        InstalledBuildText.Text=_currentBuild>0
            ?"安装版本已从本机安装文件确认。"
            :"旧版没有内置构建编号，首次升级后可精确比较新旧构建。";
    }

    static string? Property(JsonElement item,string name)
    {
        if(item.ValueKind!=JsonValueKind.Object ||
           !item.TryGetProperty(name,out var value) ||
           value.ValueKind!=JsonValueKind.String)return null;
        return value.GetString();
    }

    async Task<JsonDocument> ReadJsonAsync(string url,CancellationToken ct)
    {
        using var response=await _http.GetAsync(url,ct);
        response.EnsureSuccessStatusCode();
        await using var stream=await response.Content.ReadAsStreamAsync(ct);
        return await JsonDocument.ParseAsync(stream,cancellationToken:ct);
    }

    void UpdateButtons()
    {
        CheckButton.IsEnabled=!_checking && !_downloading;
        DownloadButton.IsEnabled=!_checking && !_downloading &&
            _latestBuild>0 && (_currentBuild==0 || _latestBuild>_currentBuild);
        OpenFolderButton.IsEnabled=!string.IsNullOrEmpty(_downloaded) &&
            File.Exists(_downloaded);
    }

    static string CachePath=>Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "SignalSchedulerData","update-manifest-cache.json");

    static VerifiedUpdateManifest? TryLoadCache()
    {
        try
        {
            var file=CachePath;
            if(!File.Exists(file) ||
               DateTime.UtcNow-File.GetLastWriteTimeUtc(file)>TimeSpan.FromDays(7))
                return null;
            return UpdateManifestCodec.Parse(File.ReadAllText(file));
        }
        catch(Exception ex) when(ex is IOException or UnauthorizedAccessException)
        {
            return null;
        }
    }

    static void SaveCache(VerifiedUpdateManifest manifest)
    {
        try
        {
            var file=CachePath;
            Directory.CreateDirectory(Path.GetDirectoryName(file)!);
            var temporary=file+".new";
            File.WriteAllText(temporary,UpdateManifestCodec.Serialize(manifest));
            File.Move(temporary,file,true);
        }
        catch(Exception ex) when(ex is IOException or UnauthorizedAccessException)
        {
            // Unable to cache does not invalidate verified online metadata.
        }
    }

    void ApplyManifest(VerifiedUpdateManifest manifest)
    {
        _latestBuild=manifest.Build;
        _latestVersion=manifest.Version;
        _latestSha=manifest.SourceSha;
        _latestRunUrl=manifest.RunUrl;
        _expectedSha256=manifest.InstallerSha256;
        _downloadUrl=null;
        _checksumUrl=null;
        _releaseName=null;
        LatestVersionText.Text=$"V{manifest.Version} · Build #{manifest.Build}";
    }

    Task<VerifiedUpdateManifest> ReadStaticManifestAsync(CancellationToken ct)=>
        UpdateFeedClient.FetchAsync(_http,ct);

    // This HEAD only checks if a published GitHub Release is present. It does
    // not download arbitrary files or consume a GitHub REST API request.
    async Task<bool> ProbeVerifiedReleaseAsync(
        VerifiedUpdateManifest manifest,CancellationToken ct)
    {
        var tag="v"+manifest.Version;
        var filename="SignalScheduler_Setup_V"+manifest.Version+".exe";
        var release=$"https://github.com/{Repo}/releases/download/{tag}/{filename}";
        try
        {
            using var request=new HttpRequestMessage(HttpMethod.Head,release);
            using var response=await _http.SendAsync(request,
                HttpCompletionOption.ResponseHeadersRead,ct);
            if(!response.IsSuccessStatusCode)return false;
            _downloadUrl=release;
            _releaseName=filename;
            _checksumUrl=null;
            return true;
        }
        catch(HttpRequestException){return false;}
        catch(TaskCanceledException){return false;}
    }

    // Legacy fallback: one REST request, never an unbounded loop. A 403 must
    // not invalidate a previously verified static manifest or cache.
    async Task<bool> TryLegacyApiFallbackAsync(CancellationToken ct)
    {
        if(DateTimeOffset.UtcNow<_nextApiRetryAt)return false;
        try
        {
            using var doc=await ReadJsonAsync(
                Api+"/actions/workflows/windows-build.yml/runs?branch=main&status=success&per_page=3",
                ct);
            if(!doc.RootElement.TryGetProperty("workflow_runs",out var runs) ||
               runs.ValueKind!=JsonValueKind.Array)return false;
            foreach(var item in runs.EnumerateArray())
            {
                if(Property(item,"head_branch")!="main" ||
                   Property(item,"conclusion")!="success" ||
                   !item.TryGetProperty("run_number",out var nValue) ||
                   !nValue.TryGetInt32(out var n) || n<=0 ||
                   !item.TryGetProperty("id",out var idValue) ||
                   !idValue.TryGetInt64(out var id) || id<=0)continue;
                _latestBuild=n;
                _latestVersion=null;
                _latestSha=Property(item,"head_sha");
                _latestRunUrl=$"https://github.com/{Repo}/actions/runs/{id}";
                LatestVersionText.Text=$"已验证构建 Build #{n}（版本号请在 GitHub 查看）";
                return true;
            }
        }
        catch(HttpRequestException ex) when(
            ex.StatusCode is HttpStatusCode.Forbidden or
            HttpStatusCode.TooManyRequests)
        {
            _nextApiRetryAt=DateTimeOffset.UtcNow.AddMinutes(15);
        }
        catch(HttpRequestException){}
        catch(TaskCanceledException){}
        return false;
    }

    async void CheckUpdates_Click(object sender,RoutedEventArgs e)
    {
        if(_checking || _downloading)return;
        _checking=true;
        DownloadButton.Content="下载更新";
        UpdateButtons();
        try
        {
            UpdateStatusText.Text="正在读取 GitHub 静态更新清单（不占用 API 查询额度）…";
            using var cts=new CancellationTokenSource(TimeSpan.FromSeconds(20));
            VerifiedUpdateManifest? manifest=null;
            string? failure=null;
            try{manifest=await ReadStaticManifestAsync(cts.Token);}
            catch(Exception ex) when(ex is HttpRequestException or
                TaskCanceledException or InvalidDataException)
            {
                failure=ex is TaskCanceledException?"网络等待超时":
                    ex is InvalidDataException?"更新清单暂不可用":"网络连接或更新清单访问失败";
            }

            if(manifest is not null)
            {
                ApplyManifest(manifest);
                SaveCache(manifest);
                if(_currentBuild>0 && _latestBuild<=_currentBuild)
                {
                    UpdateStatusText.Text="最新已验证的版本已安装，无需更新。";
                    return;
                }
                // HEAD is optional. The action-page fallback always works
                // without GitHub API credentials.
                using var headTimeout=new CancellationTokenSource(TimeSpan.FromSeconds(7));
                if(await ProbeVerifiedReleaseAsync(manifest,headTimeout.Token))
                {
                    DownloadButton.Content="下载并校验安装包";
                    UpdateStatusText.Text="已读取最新构建，发现 GitHub 正式发布安装包。下载后自动核对 CI 生成的 SHA256。";
                }
                else
                {
                    DownloadButton.Content="在 GitHub 获取安装包";
                    UpdateStatusText.Text="已从 GitHub 静态清单确认最新成功构建。此版本尚未发布独立下载地址，请在 Actions 页面底部 Artifacts 下载。";
                }
                return;
            }

            // Even under quota exhaustion, keep the known good answer rather
            // than saying "检查失败" and disabling the download link.
            var cached=TryLoadCache();
            if(cached is not null)
            {
                ApplyManifest(cached);
                DownloadButton.Content="打开已缓存的 GitHub 构建";
                UpdateStatusText.Text="在线检查暂不可用（"+failure+
                    "），这里显示的是最近 7 天缓存记录，不能证明当前仍为最新版。可点击下载按钮打开对应构建，或使用“GitHub 网页检查”。";
                return;
            }
            // API fallback only matters when the new raw feed has not been
            // deployed yet; its failure is not fatal to manual updates.
            if(await TryLegacyApiFallbackAsync(cts.Token))
            {
                DownloadButton.Content="在 GitHub 获取安装包";
                UpdateStatusText.Text="静态清单暂不可用，已从 GitHub API 获取最近一次成功构建。可在 Actions 下载，或使用 GitHub 网页检查。";
                return;
            }

            _latestBuild=0;
            _latestRunUrl=null;
            LatestVersionText.Text="未能核实最新版本";
            UpdateStatusText.Text="GitHub 静态更新源暂不可用，备用 API 也可能受到 403 限流。"+
                "这不代表软件或安装包损坏。请点击“GitHub 网页检查”，仍可手动查看官方成功构建。";
        }
        catch(Exception)
        {
            // A UI update check must never crash the WPF event handler.
            LatestVersionText.Text=_latestBuild>0
                ?LatestVersionText.Text:"未能核实最新版本";
            UpdateStatusText.Text="更新检查暂时无法完成。请使用“GitHub 网页检查”直接打开官方构建列表。";
        }
        finally
        {
            _checking=false;
            UpdateButtons();
        }
    }

    void OpenGithub_Click(object sender,RoutedEventArgs e)
    {
        OpenOfficialUrl(WorkflowUrl);
    }

    static void OpenOfficialUrl(string? url)
    {
        if(string.IsNullOrWhiteSpace(url) ||
           !url.StartsWith("https://github.com/mosen6266-netizen/MS002/",
               StringComparison.OrdinalIgnoreCase))return;
        Process.Start(new ProcessStartInfo(url){UseShellExecute=true});
    }

    async void DownloadUpdate_Click(object sender,RoutedEventArgs e)
    {
        if(_checking || _downloading || _latestBuild<=0 ||
           (_currentBuild>0 && _latestBuild<=_currentBuild))return;
        if(_downloadUrl is null || _releaseName is null ||
           string.IsNullOrWhiteSpace(_expectedSha256))
        {
            OpenOfficialUrl(_latestRunUrl??WorkflowUrl);
            return;
        }
        if(!Regex.IsMatch(_releaseName,@"^SignalScheduler_Setup_V8[\w.\-]+\.exe$",
            RegexOptions.CultureInvariant))return;
        _downloading=true;
        DownloadProgress.Visibility=Visibility.Visible;
        DownloadProgress.Value=0;
        UpdateButtons();
        string? temp=null;
        try
        {
            using var token=new CancellationTokenSource(TimeSpan.FromMinutes(20));
            // Expected hash comes from the CI-verified GitHub update feed;
            // a release download is accepted only if its bytes match.
            var expected=_expectedSha256!;

            var folder=Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
                "Downloads","MS002");
            Directory.CreateDirectory(folder);
            var path=Path.Combine(folder,_releaseName);
            temp=path+".partial";
            using var response=await _http.GetAsync(_downloadUrl,
                HttpCompletionOption.ResponseHeadersRead,token.Token);
            response.EnsureSuccessStatusCode();
            const long maxBytes=700L*1024*1024;
            if(response.Content.Headers.ContentLength is long length &&
               length>maxBytes)throw new InvalidDataException("安装包超过 700 MB 上限。");
            await using(var input=await response.Content.ReadAsStreamAsync(token.Token))
            await using(var output=new FileStream(temp,FileMode.Create,
                FileAccess.Write,FileShare.None,81920,true))
            {
                var bytes=new byte[131072];
                long total=0;
                int read;
                while((read=await input.ReadAsync(bytes,token.Token))>0)
                {
                    total+=read;
                    if(total>maxBytes)
                        throw new InvalidDataException("安装包超过下载大小限制。");
                    await output.WriteAsync(bytes.AsMemory(0,read),token.Token);
                    if(response.Content.Headers.ContentLength is long size && size>0)
                        DownloadProgress.Value=Math.Clamp(total*100d/size,0,100);
                }
            }
            await using var verify=File.OpenRead(temp);
            var actual=Convert.ToHexString(
                await SHA256.HashDataAsync(verify,token.Token));
            if(!string.Equals(actual,expected,StringComparison.OrdinalIgnoreCase))
                throw new InvalidDataException("安装包 SHA256 校验失败，未保留下载文件。");
            File.Move(temp,path,true);
            temp=null;
            _downloaded=path;
            DownloadProgress.Value=100;
            UpdateStatusText.Text="新版安装包已下载并通过 SHA256 校验："+path+
                "。请先停止所有运行任务并关闭 MS002，再执行安装。";
        }
        catch(Exception ex)
        {
            UpdateStatusText.Text="下载失败："+ex.Message+
                "。可稍后重试，或在 GitHub 中手动下载。";
        }
        finally
        {
            try{if(temp is not null && File.Exists(temp))File.Delete(temp);}
            catch(IOException){}
            _downloading=false;
            UpdateButtons();
        }
    }

    void OpenDownloaded_Click(object sender,RoutedEventArgs e)
    {
        if(string.IsNullOrWhiteSpace(_downloaded) || !File.Exists(_downloaded))return;
        Process.Start(new ProcessStartInfo("explorer.exe")
        {
            Arguments="/select,\""+_downloaded+"\"",
            UseShellExecute=true
        });
    }
}
