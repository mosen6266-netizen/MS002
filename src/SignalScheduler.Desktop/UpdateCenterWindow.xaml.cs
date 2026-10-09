using System.Diagnostics;
using System.IO;
using System.Net.Http;
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
    const string CurrentProductVersion="8.0.0-beta.6";
    static readonly string Api="https://api.github.com/repos/"+Repo;
    readonly HttpClient _http=new(){Timeout=Timeout.InfiniteTimeSpan};
    bool _checking;
    bool _downloading;
    int _currentBuild;
    int _latestBuild;
    string? _latestRunUrl;
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

    async void CheckUpdates_Click(object sender,RoutedEventArgs e)
    {
        if(_checking || _downloading)return;
        _checking=true;
        _latestBuild=0;
        _latestRunUrl=null;
        _latestSha=null;
        _downloadUrl=null;
        _checksumUrl=null;
        _releaseName=null;
        DownloadButton.Content="下载更新";
        UpdateButtons();
        try
        {
            UpdateStatusText.Text="正在读取 GitHub main 分支最新成功的 Windows 安装构建…";
            using var cts=new CancellationTokenSource(TimeSpan.FromSeconds(20));
            using var doc=await ReadJsonAsync(
                Api+"/actions/workflows/windows-build.yml/runs?branch=main&status=success&per_page=10",
                cts.Token);
            if(!doc.RootElement.TryGetProperty("workflow_runs",out var runs) ||
               runs.ValueKind!=JsonValueKind.Array)
                throw new InvalidDataException("GitHub 没有返回有效的构建列表。");
            foreach(var item in runs.EnumerateArray())
            {
                if(Property(item,"head_branch")!="main" ||
                   Property(item,"conclusion")!="success" ||
                   !item.TryGetProperty("run_number",out var number) ||
                   !number.TryGetInt32(out var n) || n<=0)continue;
                if(!item.TryGetProperty("id",out var idProp) ||
                   !idProp.TryGetInt64(out var id) || id<=0)continue;
                _latestBuild=n;
                _latestSha=Property(item,"head_sha");
                // Generate URL from numeric GitHub ID, not untrusted JSON URLs.
                _latestRunUrl=$"https://github.com/{Repo}/actions/runs/{id}";
                break;
            }
            if(_latestBuild<=0)
            {
                LatestVersionText.Text="没有找到成功的发布构建";
                UpdateStatusText.Text="当前未能确定可下载的新版本。请稍后重试。";
                return;
            }
            LatestVersionText.Text=$"V{CurrentProductVersion} · Build #{_latestBuild}";
            if(_currentBuild>0 && _latestBuild<=_currentBuild)
            {
                UpdateStatusText.Text="当前已经是最新成功构建，无需下载更新。";
                return;
            }

            // Official Releases are directly downloadable; only accept a
            // release explicitly tied to the latest successful CI commit.
            try
            {
                using var releases=await ReadJsonAsync(Api+"/releases?per_page=20",cts.Token);
                if(releases.RootElement.ValueKind==JsonValueKind.Array)
                {
                    foreach(var rel in releases.RootElement.EnumerateArray())
                    {
                        if(rel.TryGetProperty("draft",out var draft) &&
                           draft.ValueKind==JsonValueKind.True)continue;
                        if(Property(rel,"target_commitish")!=_latestSha)continue;
                        if(!rel.TryGetProperty("assets",out var assets) ||
                           assets.ValueKind!=JsonValueKind.Array)continue;
                        string? download=null,checksum=null,filename=null;
                        foreach(var asset in assets.EnumerateArray())
                        {
                            var name=Property(asset,"name");
                            var url=Property(asset,"browser_download_url");
                            if(string.IsNullOrEmpty(name)||string.IsNullOrEmpty(url)||
                               !url.StartsWith(
                               "https://github.com/"+Repo+"/releases/download/",
                               StringComparison.OrdinalIgnoreCase))continue;
                            if(name.StartsWith("SignalScheduler_Setup_V8",StringComparison.Ordinal) &&
                               name.EndsWith(".exe",StringComparison.OrdinalIgnoreCase))
                            {
                                download=url;filename=name;
                            }
                            else if(name=="INSTALLER_SHA256.txt")
                                checksum=url;
                        }
                        if(download is not null && checksum is not null)
                        {
                            _downloadUrl=download;
                            _checksumUrl=checksum;
                            _releaseName=filename;
                            break;
                        }
                    }
                }
            }
            catch(HttpRequestException){ /* CI artifact fallback remains available. */ }
            catch(TaskCanceledException){ /* CI artifact fallback remains available. */ }
            if(_downloadUrl is not null)
            {
                DownloadButton.Content="下载校验过的安装包";
                UpdateStatusText.Text="发现较新版本，并找到 GitHub Releases 官方安装包。"+
                    "可直接下载到本机，下载后将进行 SHA256 校验。";
            }
            else
            {
                DownloadButton.Content="在 GitHub 获取更新";
                UpdateStatusText.Text="发现较新成功构建。该构建尚无匹配的 Releases 安装包，"+
                    "点击下载将打开 GitHub Actions 的 Artifacts，使用浏览器下载。";
            }
        }
        catch(Exception ex)
        {
            LatestVersionText.Text="检查失败";
            UpdateStatusText.Text="无法读取 GitHub 最新版本："+ex.Message;
        }
        finally
        {
            _checking=false;
            UpdateButtons();
        }
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
        if(_downloadUrl is null || _checksumUrl is null || _releaseName is null)
        {
            OpenOfficialUrl(_latestRunUrl);
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
            using var hashResponse=await _http.GetAsync(_checksumUrl,token.Token);
            hashResponse.EnsureSuccessStatusCode();
            var manifest=await hashResponse.Content.ReadAsStringAsync(token.Token);
            var hashMatch=Regex.Match(manifest,@"\b[a-fA-F0-9]{64}\b");
            if(!hashMatch.Success)
                throw new InvalidDataException("发布文件缺少有效的 SHA256 校验值。");
            var expected=hashMatch.Value;

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
