using System.Diagnostics;
using System.Net.Http.Json;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Microsoft.Extensions.Hosting;
using SignalScheduler.Engine;
using SignalScheduler.Shared;

namespace SignalScheduler.Engine.Licensing;

public sealed class LicenseManager : BackgroundService
{
    const string Server="https://signal-scheduler-license.mosen6266-ms007.workers.dev/";
    const string Version="9.0.3";

    readonly RuntimePaths _paths;
    readonly HttpClient _client;
    readonly SemaphoreSlim _gate=new(1,1);
    readonly object _statusLock=new();
    LicensePublicStatus _status=new("unknown","尚未检查卡密。","",0,0,0,false,false);
    // Anchored only by a freshly received, signature-verified online lease.
    // Monotonic elapsed time avoids relying on Windows timezone or manual clock
    // changes for dispatch while the current Engine process is running.
    long _verifiedServerUnixSeconds;
    long _verifiedServerStopwatchTicks;

    sealed record SavedLicense(
        string LicenseKey,string DeviceId,string Lease,
        string Signature,string PublicKeyJson,string PublicKeyPin,
        string TypeName,long ActivatedAt);

    public LicenseManager(RuntimePaths paths)
        :this(paths,new HttpClient{BaseAddress=new Uri(Server),
          Timeout=TimeSpan.FromSeconds(9)})
    {
    }

    public LicenseManager(RuntimePaths paths,HttpClient client)
    {
        _paths=paths;
        _client=client;
    }

    public LicensePublicStatus Status
    {
        get {lock(_statusLock)return _status;}
    }
    void SetStatus(LicensePublicStatus status)
    {
        lock(_statusLock)_status=status;
    }

    void RememberSignedServerTime(long signedIssuedAt)
    {
        lock(_statusLock)
        {
            _verifiedServerUnixSeconds=signedIssuedAt;
            _verifiedServerStopwatchTicks=Stopwatch.GetTimestamp();
        }
    }

    /// <summary>
    /// A fresh online response establishes a trusted UTC anchor for this
    /// process. Stopwatch time stays monotonic across user timezone changes.
    /// After restart without a successful online check, fall back to UTC for
    /// conservative offline lease checks; never infer unlimited validity.
    /// </summary>
    public long LicenseNowUnixSeconds()
    {
        lock(_statusLock)
        {
            if(_verifiedServerUnixSeconds>0 && _verifiedServerStopwatchTicks>0)
                return _verifiedServerUnixSeconds+
                    (long)Stopwatch.GetElapsedTime(_verifiedServerStopwatchTicks).TotalSeconds;
        }
        return DateTimeOffset.UtcNow.ToUnixTimeSeconds();
    }

    string KeyPath=>Path.Combine(_paths.DataRoot,"license-v8.dpapi");
    string DevicePath=>Path.Combine(_paths.DataRoot,"device-v8.dpapi");

    static byte[] Protect(byte[] bytes)=>
        ProtectedData.Protect(bytes,null,DataProtectionScope.CurrentUser);
    static byte[] Unprotect(byte[] bytes)=>
        ProtectedData.Unprotect(bytes,null,DataProtectionScope.CurrentUser);

    async Task<string> GetDeviceAsync(CancellationToken ct)
    {
        if(File.Exists(DevicePath))
        {
            try
            {
                var bytes=await File.ReadAllBytesAsync(DevicePath,ct);
                var device=Encoding.UTF8.GetString(Unprotect(bytes));
                if(Guid.TryParseExact(device,"N",out _))return device;
                throw new CryptographicException(
                    "已有本机设备标识格式异常，未生成新设备。请检查 Windows 登录账户及本地授权数据。");
            }
            catch(CryptographicException ex)
            {
                throw new CryptographicException(
                    "无法读取原设备绑定资料，已阻止自动生成新设备标识。请使用原 Windows 用户登录，不要删除旧授权或重复生成卡密。",ex);
            }
        }
        var id=Guid.NewGuid().ToString("N");
        await AtomicWriteAsync(DevicePath,
            Protect(Encoding.UTF8.GetBytes(id)),ct);
        return id;
    }

    static async Task AtomicWriteAsync(string path,byte[] bytes,CancellationToken ct)
    {
        var tmp=path+$".{Guid.NewGuid():N}.tmp";
        try
        {
            await File.WriteAllBytesAsync(tmp,bytes,ct);
            File.Move(tmp,path,true);
        }
        finally
        {
            try{if(File.Exists(tmp))File.Delete(tmp);}catch{}
        }
    }

    async Task<SavedLicense?> ReadSavedAsync(CancellationToken ct)
    {
        if(!File.Exists(KeyPath)) return null;
        var encrypted=await File.ReadAllBytesAsync(KeyPath,ct);
        var clear=Unprotect(encrypted);
        try {return JsonSerializer.Deserialize<SavedLicense>(clear);}
        finally{CryptographicOperations.ZeroMemory(clear);}
    }

    static string KeyNormalize(string key)
    {
        var normalized=new string(key.Trim().Where(x=>!char.IsWhiteSpace(x))
            .Select(char.ToUpperInvariant).ToArray());
        if(normalized.Length is <6 or >140)
            throw new ArgumentException("卡密长度无效。");
        return normalized;
    }

    public async Task<LicensePublicStatus> ActivateAsync(
        LicenseActivationRequest request,CancellationToken ct)
    {
        if(request is null || string.IsNullOrWhiteSpace(request.LicenseKey))
            throw new ArgumentException("请输入卡密。");
        var key=KeyNormalize(request.LicenseKey);
        await _gate.WaitAsync(ct);
        try
        {
            var device=await GetDeviceAsync(ct);
            // The Worker may commit first activation even if the HTTPS reply
            // is lost. Re-query that exact key/device once before reporting
            // failure, so a completed binding can be recovered idempotently.
            var response=await ActivateOrRecoverAsync(key,device,ct);
            var claims=LicenseLeaseVerifier.Verify(
                response.GetProperty("lease").GetString()!,
                response.GetProperty("signature").GetString()!,
                response.GetProperty("public_key"),device,null,
                DateTimeOffset.UtcNow.ToUnixTimeSeconds(),
                trustedOnlineResponse:true);
            RememberSignedServerTime(claims.IssuedAt);
            var saved=new SavedLicense(key,device,
                response.GetProperty("lease").GetString()!,
                response.GetProperty("signature").GetString()!,
                response.GetProperty("public_key").GetRawText(),claims.PublicKeyPin,
                response.GetProperty("type_name").GetString()??claims.CardType,
                response.GetProperty("activated_at").GetInt64());
            await AtomicWriteAsync(KeyPath,Protect(JsonSerializer.SerializeToUtf8Bytes(saved)),ct);
            var status=ToStatus(saved,claims,true,"授权激活成功。");
            SetStatus(status);
            return status;
        }
        finally{_gate.Release();}
    }

    public async Task<LicensePublicStatus> CheckAsync(CancellationToken ct)
    {
        await _gate.WaitAsync(ct);
        try
        {
            SavedLicense? saved;
            try{saved=await ReadSavedAsync(ct);}
            catch(Exception ex) when(ex is IOException or CryptographicException
                or JsonException)
            {
                var invalid=new LicensePublicStatus("invalid",
                    "本地授权资料无法解密或已损坏，需重新激活。","",0,0,0,false,true);
                SetStatus(invalid);
                return invalid;
            }
            if(saved is null)
            {
                var none=new LicensePublicStatus("unactivated",
                    "当前没有已保存的 V8 卡密，旧版资料不会被删除。","",0,0,0,false,false);
                SetStatus(none);
                return none;
            }

            try
            {
                var device=await GetDeviceAsync(ct);
                if(saved.DeviceId!=device)
                    throw new CryptographicException("授权设备标识不一致。");
                var response=await PostAsync("api/license/check",
                    saved.LicenseKey,device,ct);
                var publicKey=response.GetProperty("public_key");
                var claims=LicenseLeaseVerifier.Verify(
                    response.GetProperty("lease").GetString()!,
                    response.GetProperty("signature").GetString()!,
                    publicKey,device,saved.PublicKeyPin,
                    DateTimeOffset.UtcNow.ToUnixTimeSeconds(),
                    trustedOnlineResponse:true);
                RememberSignedServerTime(claims.IssuedAt);
                var renewed=saved with
                {
                    Lease=response.GetProperty("lease").GetString()!,
                    Signature=response.GetProperty("signature").GetString()!,
                    PublicKeyJson=publicKey.GetRawText()
                };
                await AtomicWriteAsync(KeyPath,
                    Protect(JsonSerializer.SerializeToUtf8Bytes(renewed)),ct);
                var ok=ToStatus(renewed,claims,true,"授权在线验证成功。");
                SetStatus(ok);
                return ok;
            }
            catch(HttpRequestException)
            {
                return UseCachedLease(saved,"暂时无法访问授权服务器。");
            }
            catch(TaskCanceledException) when(!ct.IsCancellationRequested)
            {
                return UseCachedLease(saved,"网络响应超时。");
            }
            catch(Exception ex) when(ex is IOException or CryptographicException
                or JsonException or FormatException or InvalidOperationException)
            {
                var denied=new LicensePublicStatus("invalid",
                    ex.Message,"",0,0,0,true,true);
                SetStatus(denied);
                return denied;
            }
        }
        finally{_gate.Release();}
    }

    LicensePublicStatus UseCachedLease(SavedLicense saved,string reason)
    {
        try
        {
            using var key=JsonDocument.Parse(saved.PublicKeyJson);
            var claims=LicenseLeaseVerifier.Verify(
                saved.Lease,saved.Signature,key.RootElement,
                saved.DeviceId,saved.PublicKeyPin,
                LicenseNowUnixSeconds());
            var cached=ToStatus(saved,claims,false,
                $"{reason} 已验证本地签名，仅在剩余授权租期内临时有效。");
            SetStatus(cached);
            return cached;
        }
        catch(Exception)
        {
            var denied=new LicensePublicStatus("offline-expired",
                $"{reason} 本地授权已过期或损坏，请联网验证。","",0,0,0,false,true);
            SetStatus(denied);
            return denied;
        }
    }

    static LicensePublicStatus ToStatus(
        SavedLicense saved,VerifiedLicenseLease claims,bool connected,string detail)=>
        new(connected?"active":"active-offline",
            detail,saved.TypeName,saved.ActivatedAt,claims.ExpiresAt,
            claims.LeaseUntil,connected,true);

    async Task<JsonElement> ActivateOrRecoverAsync(string key,string device,CancellationToken ct)
    {
        try
        {
            return await PostAsync("api/license/activate",key,device,ct);
        }
        catch(Exception ex) when(ex is HttpRequestException ||
            ex is TaskCanceledException && !ct.IsCancellationRequested)
        {
            // No new device is created and no alternate card is generated.
            // /check succeeds only when this exact device was already bound
            // by the first request, and still returns a signed current lease.
            try
            {
                return await PostAsync("api/license/check",key,device,ct);
            }
            catch(Exception checkEx) when(checkEx is HttpRequestException or
                TaskCanceledException or InvalidOperationException)
            {
                throw new IOException(
                    "激活请求可能已到达服务器，但未能确认回执。请联网后使用原卡密在同一台电脑重试，不要另外生成新卡。",ex);
            }
        }
    }

    async Task<JsonElement> PostAsync(
        string endpoint,string key,string device,CancellationToken ct)
    {
        using var request=new HttpRequestMessage(HttpMethod.Post,endpoint)
        {
            Content=JsonContent.Create(new
            {
                license_key=key,device_id=device,app_version=Version
            })
        };
        using var response=await _client.SendAsync(request,ct);
        var raw=await response.Content.ReadAsStringAsync(ct);
        using var doc=JsonDocument.Parse(raw);
        var result=doc.RootElement.Clone();
        if(!response.IsSuccessStatusCode ||
            !result.TryGetProperty("ok",out var success) ||
            !success.GetBoolean())
        {
            var error=result.TryGetProperty("error",out var err)
                ?err.GetString():"server_rejected";
            throw new InvalidOperationException($"授权服务器拒绝：{error}。");
        }
        return result;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        while(!stoppingToken.IsCancellationRequested)
        {
            try {await CheckAsync(stoppingToken);}
            catch(OperationCanceledException) when(stoppingToken.IsCancellationRequested)
            {
                break;
            }
            catch(Exception ex)
            {
                SetStatus(new LicensePublicStatus("error",
                    $"授权检查异常：{ex.GetType().Name}","",0,0,0,false,File.Exists(KeyPath)));
            }
            try{await Task.Delay(TimeSpan.FromMinutes(30),stoppingToken);}
            catch(OperationCanceledException) when(stoppingToken.IsCancellationRequested)
            {
                break;
            }
        }
    }
}
