using System.IO.Compression;
using System.Security.Cryptography;
using System.Text.Json;

namespace SignalScheduler.Shared;

/// <summary>
/// Read-only verification of the *installed* Java/signal-cli distribution.
/// The CI installer builds an SHA-256 manifest from the verified release assets.
/// This is an operational corruption/quarantine check, not a replacement for
/// release authentication or an antivirus scan. Never inspect user account data.
/// </summary>
public static class RuntimeIntegrityCheck
{
    public const string ManifestName="runtime-integrity-v1.json";
    public sealed record Entry(string Path,long Length,string Sha256);
    public sealed record Manifest(int Version,List<Entry> Files);
    public sealed record Result(bool Healthy,string Code,string Detail,int FilesVerified);

    public static Result Verify(string runtimeRoot)
    {
        try
        {
            var root=Path.GetFullPath(runtimeRoot);
            var manifestPath=Path.Combine(root,ManifestName);
            if(!File.Exists(manifestPath))
                return Fail("MISSING_MANIFEST",
                    "安装包运行库校验清单缺失。请使用官方 V9.0.4 安装包覆盖修复。");
            var manifest=JsonSerializer.Deserialize<Manifest>(
                File.ReadAllText(manifestPath),
                new JsonSerializerOptions{PropertyNameCaseInsensitive=true});
            if(manifest is null || manifest.Version!=1 || manifest.Files is null ||
                manifest.Files.Count<2 || manifest.Files.Count>1000)
                return Fail("INVALID_MANIFEST","运行库校验清单格式不正确，请重新安装。");

            var count=0;
            var java=false;
            var jars=0;
            var argparseClass=false;
            var argparseResource=false;
            var signalMain=false;
            var seen=new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach(var entry in manifest.Files)
            {
                if(string.IsNullOrWhiteSpace(entry.Path) ||
                   entry.Path.StartsWith("/",StringComparison.Ordinal) ||
                   entry.Path.Contains('\\') || entry.Path.Split('/').Any(
                       part=>part is "" or "." or "..") ||
                   entry.Path.Any(c=>!(char.IsAsciiLetterOrDigit(c) ||
                       c is '/' or '_' or '-' or '.')) ||
                   !seen.Add(entry.Path) || entry.Length<=0 ||
                   entry.Sha256 is null || entry.Sha256.Length!=64 ||
                   !entry.Sha256.All(Uri.IsHexDigit))
                    return Fail("INVALID_MANIFEST","安装包校验清单包含无效文件条目。");

                var file=Path.GetFullPath(Path.Combine(root,
                    entry.Path.Replace('/',Path.DirectorySeparatorChar)));
                if(!file.StartsWith(root+Path.DirectorySeparatorChar,
                       StringComparison.OrdinalIgnoreCase))
                    return Fail("INVALID_MANIFEST","安装包校验清单路径不安全。");
                var name=Path.GetFileName(file);
                if(!File.Exists(file))
                    return Fail("MISSING_FILE",$"缺少运行库文件：{name}。可能被安全软件隔离或安装不完整，请检查隔离记录并使用官方安装包修复。");
                var info=new FileInfo(file);
                if(info.Length!=entry.Length)
                    return Fail("CORRUPT_FILE",$"运行库文件大小异常：{name}。请使用官方安装包修复。");
                using(var stream=File.OpenRead(file))
                {
                    var actual=Convert.ToHexString(SHA256.HashData(stream));
                    if(!actual.Equals(entry.Sha256,StringComparison.OrdinalIgnoreCase))
                        return Fail("CORRUPT_FILE",$"运行库文件校验不匹配：{name}。请检查安全软件隔离记录并重新覆盖安装。");
                }
                count++;
                if(entry.Path.Equals("jre/bin/java.exe",StringComparison.OrdinalIgnoreCase))
                    java=true;
                if(!entry.Path.StartsWith("signal-cli/lib/",StringComparison.OrdinalIgnoreCase)
                   || !entry.Path.EndsWith(".jar",StringComparison.OrdinalIgnoreCase))
                    continue;
                jars++;
                using var archive=ZipFile.OpenRead(file);
                if(archive.GetEntry("org/asamk/signal/Main.class") is not null)
                    signalMain=true;
                if(archive.GetEntry("net/sourceforge/argparse4j/internal/ArgumentParserImpl.class") is not null)
                {
                    argparseClass=true;
                    if(archive.GetEntry("net/sourceforge/argparse4j/internal/ArgumentParserImpl.properties") is not null ||
                       archive.GetEntry("net/sourceforge/argparse4j/internal/ArgumentParserImpl_en.properties") is not null)
                        argparseResource=true;
                }
            }

            // A wildcard -classpath lib/* loads EVERY JAR in the directory.
            // Inno's previous ignoreversion overlay left old JARs behind,
            // and the manifest could declare success without checking them.
            // Unknown JARs can supply shadow copies of argparse4j classes.
            var expectedJars=manifest.Files
                .Where(e=>e.Path.StartsWith("signal-cli/lib/",StringComparison.OrdinalIgnoreCase)
                       && e.Path.EndsWith(".jar",StringComparison.OrdinalIgnoreCase))
                .Select(e=>Path.GetFileName(e.Path))
                .ToHashSet(StringComparer.OrdinalIgnoreCase);
            var jarDirectory=Path.Combine(root,"signal-cli","lib");
            foreach(var extra in Directory.EnumerateFiles(jarDirectory,"*.jar",
                SearchOption.TopDirectoryOnly))
            {
                if(!expectedJars.Contains(Path.GetFileName(extra)))
                    return Fail("EXTRA_JAR",
                        "发现安装清单以外的旧运行库："+Path.GetFileName(extra)+
                        "。旧版本覆盖安装可能留下冲突文件，请关闭程序后使用新版官方安装包覆盖修复。");
            }
            if(!java || jars==0 || !signalMain)
                return Fail("INCOMPLETE_RUNTIME","内置 Java 或 signal-cli 主程序不完整，请使用官方安装包修复。");
            if(!argparseClass || !argparseResource)
                return Fail("MISSING_RESOURCE",
                    "argparse4j 命令行解析资源缺失，不能启动 Signal。请使用官方安装包修复，不要删除账号数据。");
            return new Result(true,"OK",$"已校验 {count} 个运行库文件及 argparse4j 必需资源。",count);
        }
        catch(Exception ex) when(ex is IOException or UnauthorizedAccessException
            or JsonException or InvalidDataException or NotSupportedException
            or ArgumentException)
        {
            return Fail("INTEGRITY_ERROR",
                "运行库无法完整读取或解压，可能文件损坏或被防护软件锁定。请检查隔离记录并修复安装。");
        }
    }

    static Result Fail(string code,string detail)=>new(false,code,detail,0);
}
