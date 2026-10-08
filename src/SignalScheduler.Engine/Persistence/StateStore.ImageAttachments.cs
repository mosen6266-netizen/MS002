using System.Security.Cryptography;
using System.Text.RegularExpressions;
using SignalScheduler.Shared;

namespace SignalScheduler.Engine.Persistence;

public sealed partial class StateStore
{
    static readonly Regex ImageReferencePattern=new(
        @"^img:([a-f0-9]{64})\.(png|jpg|gif|bmp)$",
        RegexOptions.CultureInvariant|RegexOptions.Compiled);

    static readonly HashSet<string> AllowedImageExtensions=new(
        new[]{".png",".jpg",".jpeg",".gif",".bmp"},
        StringComparer.OrdinalIgnoreCase);

    string ImageRoot=>Path.Combine(_paths.DataRoot,"attachments","images");

    /// <summary>
    /// Copy a selected image into the durable per-user data directory.
    /// The stored name is content-addressed by SHA-256; it contains no
    /// attacker-supplied directory part. This method never moves/deletes the
    /// original user file, and no installer overwrites the attachments root.
    /// </summary>
    public async Task<ImageAttachmentInfo> ImportImageAsync(
        ImageImportRequest request,CancellationToken ct)
    {
        if(request is null || string.IsNullOrWhiteSpace(request.SourcePath) ||
            request.SourcePath.Length>2048 ||
            !Path.IsPathFullyQualified(request.SourcePath))
            throw new ArgumentException("请选择本地图片文件。");

        var file=new FileInfo(request.SourcePath);
        if(!file.Exists || file.Length is <1 or >15728640)
            throw new ArgumentException("图片不存在或超过 15 MB。");
        var ext=file.Extension.ToLowerInvariant();
        if(ext==".jpeg") ext=".jpg";
        if(!AllowedImageExtensions.Contains(file.Extension))
            throw new ArgumentException("目前仅支持 PNG、JPEG、GIF 和 BMP 图片。");

        Directory.CreateDirectory(ImageRoot);
        var temp=Path.Combine(ImageRoot,$".{Guid.NewGuid():N}.upload");
        var buffer=new byte[65536];
        long total=0;
        string digest;
        byte[] prefix=new byte[16];
        int prefixCount=0;
        try
        {
            await using(var source=new FileStream(request.SourcePath,FileMode.Open,
                FileAccess.Read,FileShare.Read,65536,FileOptions.Asynchronous))
            await using(var output=new FileStream(temp,FileMode.CreateNew,
                FileAccess.Write,FileShare.None,65536,FileOptions.Asynchronous))
            using(var hash=IncrementalHash.CreateHash(HashAlgorithmName.SHA256))
            {
                int count;
                while((count=await source.ReadAsync(buffer,ct))!=0)
                {
                    total+=count;
                    if(total>15728640)
                        throw new ArgumentException("图片超过 15 MB。");
                    if(prefixCount<prefix.Length)
                    {
                        var copy=Math.Min(prefix.Length-prefixCount,count);
                        Array.Copy(buffer,0,prefix,prefixCount,copy);
                        prefixCount+=copy;
                    }
                    hash.AppendData(buffer,0,count);
                    await output.WriteAsync(buffer.AsMemory(0,count),ct);
                }
                await output.FlushAsync(ct);
                output.Flush(true);
                digest=Convert.ToHexString(hash.GetHashAndReset()).ToLowerInvariant();
            }

            if(total==0 || !MatchesImageMagic(ext,prefix.AsSpan(0,prefixCount)))
                throw new ArgumentException("文件内容不是有效的受支持图片类型。");

            var filename=$"{digest}{ext}";
            var destination=Path.Combine(ImageRoot,filename);
            if(File.Exists(destination))
            {
                // Hash-addressed duplicate: preserve the already imported file.
                // Do not silently replace an existing potentially corrupted file.
                if(!await HasMatchingHashAsync(destination,digest,ct))
                    throw new IOException("已存在的附件校验失败，请人工检查本地数据。");
                File.Delete(temp);
            }
            else
            {
                try { File.Move(temp,destination); }
                catch(IOException) when(File.Exists(destination))
                {
                    if(!await HasMatchingHashAsync(destination,digest,ct)) throw;
                    File.Delete(temp);
                }
            }

            return new ImageAttachmentInfo(
                $"img:{filename}",destination,file.Name,total,digest);
        }
        finally
        {
            try { if(File.Exists(temp)) File.Delete(temp); }
            catch { }
        }
    }

    public async Task<ImageAttachmentInfo> LookupImageAsync(
        ImageLookupRequest request,CancellationToken ct)
    {
        if(request is null || string.IsNullOrWhiteSpace(request.Reference))
            throw new ArgumentException("附件编号为空。");
        var match=ImageReferencePattern.Match(request.Reference);
        if(!match.Success)
            throw new ArgumentException("附件编号格式无效，旧版文件路径请重新导入。");

        var sha=match.Groups[1].Value;
        var filename=request.Reference[4..];
        var path=Path.Combine(ImageRoot,filename);
        if(!File.Exists(path))
            throw new FileNotFoundException("图片文件不存在，可能尚未迁移到当前设备。");

        if(!await HasMatchingHashAsync(path,sha,ct))
            throw new IOException("图片校验值不一致，请不要使用损坏的图片。");

        var file=new FileInfo(path);
        if(file.Length>15728640)
            throw new IOException("附件超过文件大小限制。");
        return new ImageAttachmentInfo(request.Reference,path,
            filename,file.Length,sha);
    }

    static async Task<bool> HasMatchingHashAsync(
        string path,string expected,CancellationToken ct)
    {
        await using var input=new FileStream(path,FileMode.Open,FileAccess.Read,
            FileShare.Read,65536,FileOptions.Asynchronous);
        var actual=Convert.ToHexString(await SHA256.HashDataAsync(input,ct))
            .ToLowerInvariant();
        return string.Equals(actual,expected,StringComparison.Ordinal);
    }

    static bool MatchesImageMagic(string ext,ReadOnlySpan<byte> bytes)
    {
        static bool Starts(ReadOnlySpan<byte> actual,params byte[] signature)=>
            actual.StartsWith(signature);
        return ext switch
        {
            ".png"=>Starts(bytes,0x89,0x50,0x4E,0x47,0x0D,0x0A,0x1A,0x0A),
            ".jpg"=>Starts(bytes,0xFF,0xD8,0xFF),
            ".gif"=>Starts(bytes,0x47,0x49,0x46,0x38,0x37,0x61) ||
                    Starts(bytes,0x47,0x49,0x46,0x38,0x39,0x61),
            ".bmp"=>Starts(bytes,0x42,0x4D),
            _=>false
        };
    }
}
