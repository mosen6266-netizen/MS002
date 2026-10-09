using System.IO.Compression;
using System.Security.Cryptography;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace SignalScheduler.Shared;

/// <summary>
/// Portable, local-only script backup. No absolute paths from the archive
/// are ever used as extraction targets; image names are derived from SHA-256.
/// Original Signal identities and group membership are not transferred.
/// </summary>
public static class ScriptBundleArchive
{
    public const int MaxScripts=500;
    public const int MaxStepsPerScript=1500;
    public const int MaxImages=3000;
    public const int MaxImageBytes=15*1024*1024;
    public const long MaxArchiveBytes=512L*1024*1024;
    const int MaxManifestBytes=12*1024*1024;
    static readonly JsonSerializerOptions JsonOptions=new()
    {
        PropertyNameCaseInsensitive=true,
        WriteIndented=true
    };
    static readonly Regex ImageEntryName=new(
        @"^images/[a-f0-9]{64}\.(png|jpg|jpeg|gif|bmp)$",
        RegexOptions.CultureInvariant|RegexOptions.Compiled);

    public sealed record Manifest(int Version,ScriptSaveRequest[] Scripts,
        Dictionary<string,string> Images);
    public sealed record Contents(ScriptSaveRequest[] Scripts,
        Dictionary<string,string> ImportedImagePaths);

    static void ValidateScripts(ScriptSaveRequest[] scripts)
    {
        if(scripts.Length is <1 or >MaxScripts)
            throw new InvalidDataException("备份内的剧本数量必须为 1～500。");
        foreach(var script in scripts)
        {
            if(string.IsNullOrWhiteSpace(script.Name) || script.Name.Length>120 ||
               script.Steps is null || script.Steps.Count is <1 or >MaxStepsPerScript)
                throw new InvalidDataException("备份包含名称或消息数量无效的剧本。");
            foreach(var step in script.Steps)
            {
                if(step.DelayAfter is <0 or >3600 || step.TypingSeconds is <0 or >300)
                    throw new InvalidDataException("备份包含超出范围的时间设置。");
            }
        }
    }

    public static void Create(string destination,IReadOnlyList<ScriptSaveRequest> scripts,
        IReadOnlyDictionary<string,string> imagePaths)
    {
        var safeScripts=scripts.Select(s=>s with{ScriptId=null,Revision=0}).ToArray();
        ValidateScripts(safeScripts);
        var references=safeScripts.SelectMany(s=>s.Steps)
            .Select(x=>x.Attachment).Where(x=>!string.IsNullOrWhiteSpace(x))
            .Distinct(StringComparer.Ordinal).ToArray();
        if(references.Length>MaxImages)
            throw new InvalidDataException("备份包含过多图片。");
        if(references.Any(x=>!imagePaths.ContainsKey(x)))
            throw new InvalidDataException("存在缺失的图片路径，已中止导出。");

        var map=new Dictionary<string,string>(StringComparer.Ordinal);
        var temp=destination+".partial";
        try
        {
            if(File.Exists(temp))File.Delete(temp);
            using(var archive=ZipFile.Open(temp,ZipArchiveMode.Create))
            {
                var written=new HashSet<string>(StringComparer.Ordinal);
                foreach(var reference in references)
                {
                    var file=imagePaths[reference];
                    if(!File.Exists(file))throw new FileNotFoundException("图片不存在",file);
                    var info=new FileInfo(file);
                    if(info.Length is <1 or >MaxImageBytes)
                        throw new InvalidDataException("图片大小无效或超过 20 MB。");
                    var extension=Path.GetExtension(file).ToLowerInvariant();
                    if(extension is not (".png" or ".jpg" or ".jpeg" or ".gif" or ".bmp"))
                        throw new InvalidDataException("备份包含不支持的图片类型。");
                    using var read=File.OpenRead(file);
                    var digest=Convert.ToHexString(SHA256.HashData(read)).ToLowerInvariant();
                    var entryName="images/"+digest+extension;
                    map.Add(reference,entryName);
                    if(!written.Add(entryName))continue;
                    var entry=archive.CreateEntry(entryName,CompressionLevel.Optimal);
                    using var target=entry.Open();
                    read.Position=0;
                    read.CopyTo(target);
                }
                var manifest=new Manifest(1,safeScripts,map);
                var bytes=JsonSerializer.SerializeToUtf8Bytes(manifest,JsonOptions);
                if(bytes.Length>MaxManifestBytes)
                    throw new InvalidDataException("剧本备份清单过大。");
                var index=archive.CreateEntry("manifest.json",CompressionLevel.Optimal);
                using var stream=index.Open();
                stream.Write(bytes);
            }
            if(new FileInfo(temp).Length>MaxArchiveBytes)
                throw new InvalidDataException("压缩包超过 512 MB。");
            File.Move(temp,destination,true);
        }
        finally
        {
            if(File.Exists(temp))File.Delete(temp);
        }
    }

    public static Contents ExtractValidated(string archivePath,string stagingDirectory)
    {
        if(new FileInfo(archivePath).Length>MaxArchiveBytes)
            throw new InvalidDataException("备份压缩包超过 512 MB。");
        using var zip=ZipFile.OpenRead(archivePath);
        var index=zip.GetEntry("manifest.json")
            ??throw new InvalidDataException("备份缺少 manifest.json。");
        if(index.Length is <1 or >MaxManifestBytes)
            throw new InvalidDataException("剧本备份清单无效。");
        byte[] content;
        using(var reader=index.Open())
        using(var buffer=new MemoryStream())
        {
            var chunk=new byte[65536];
            int n;
            while((n=reader.Read(chunk,0,chunk.Length))>0)
            {
                if(buffer.Length+n>MaxManifestBytes)
                    throw new InvalidDataException("备份清单超过允许大小。");
                buffer.Write(chunk,0,n);
            }
            content=buffer.ToArray();
        }
        var manifest=JsonSerializer.Deserialize<Manifest>(content,JsonOptions)
            ??throw new InvalidDataException("备份清单无法读取。");
        if(manifest.Version!=1 || manifest.Scripts is null || manifest.Images is null)
            throw new InvalidDataException("不支持此备份格式版本。");
        ValidateScripts(manifest.Scripts);
        if(manifest.Images.Count>MaxImages)
            throw new InvalidDataException("备份图片数量超出限制。");
        var needed=manifest.Scripts.SelectMany(s=>s.Steps)
            .Select(x=>x.Attachment).Where(x=>!string.IsNullOrWhiteSpace(x))
            .Distinct(StringComparer.Ordinal).ToArray();
        if(needed.Any(x=>!manifest.Images.ContainsKey(x)))
            throw new InvalidDataException("备份缺少消息对应的图片映射。");
        Directory.CreateDirectory(stagingDirectory);
        var extracted=new Dictionary<string,string>(StringComparer.Ordinal);
        foreach(var original in needed)
        {
            var entryName=manifest.Images[original];
            if(!ImageEntryName.IsMatch(entryName))
                throw new InvalidDataException("备份含不安全的图片条目名称。");
            var entry=zip.GetEntry(entryName)
                ??throw new InvalidDataException("备份缺少图片："+entryName);
            if(entry.Length is <1 or >MaxImageBytes)
                throw new InvalidDataException("图片文件大小无效或超限。");
            // Only the validated hash-based filename is ever written.
            var path=Path.Combine(stagingDirectory,Path.GetFileName(entryName));
            if(!File.Exists(path))
            {
                using var output=File.Create(path);
                using var input=entry.Open();
                var chunk=new byte[65536];
                long extractedBytes=0;
                int n;
                while((n=input.Read(chunk,0,chunk.Length))>0)
                {
                    extractedBytes+=n;
                    if(extractedBytes>MaxImageBytes || extractedBytes>entry.Length)
                        throw new InvalidDataException("压缩包图片展开后超过允许大小。");
                    output.Write(chunk,0,n);
                }
            }
            if(new FileInfo(path).Length!=entry.Length)
                throw new InvalidDataException("图片文件长度不匹配。");
            using var image=File.OpenRead(path);
            var digest=Convert.ToHexString(SHA256.HashData(image)).ToLowerInvariant();
            if(!entryName.StartsWith("images/"+digest+".",StringComparison.Ordinal))
                throw new InvalidDataException("图片 SHA256 校验失败，已拒绝导入。");
            extracted[original]=path;
        }
        return new Contents(manifest.Scripts,extracted);
    }
}
