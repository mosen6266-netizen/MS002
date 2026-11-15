using System.Security.AccessControl;
using System.Security.Cryptography;
using System.Security.Principal;
using SignalScheduler.Shared;

namespace SignalScheduler.Engine;

/// <summary>
/// Some Windows Java launchers cannot load java.dll when the executable path
/// contains characters outside the active system ANSI code page. Preserve
/// the user-selected installation directory and copy ONLY the verified,
/// immutable application runtime to a protected ASCII-only location.
/// User Signal accounts, license state, task history and scripts are untouched.
/// </summary>
internal static class JavaRuntimePath
{
    public static string Resolve(string bundledRuntime)
    {
        if(!OperatingSystem.IsWindows() || IsAscii(bundledRuntime))
            return bundledRuntime;

        var integrity=RuntimeIntegrityCheck.Verify(bundledRuntime);
        if(!integrity.Healthy)
            throw new IOException("安装目录运行库校验失败："+integrity.Detail);

        var commonDocuments=Environment.GetFolderPath(
            Environment.SpecialFolder.CommonDocuments);
        if(string.IsNullOrWhiteSpace(commonDocuments) || !IsAscii(commonDocuments))
            throw new IOException("系统公共文档目录包含非英文字符，无法安全放置兼容 Java 运行库。");

        var user=WindowsIdentity.GetCurrent().User ??
            throw new UnauthorizedAccessException("无法读取当前 Windows 用户身份。");

        // Public Documents is usable by standard Windows user accounts.
        // Use the user's SID in a folder protected against other users
        // writing arbitrary executable JARs or native DLLs.
        var parent=Path.Combine(commonDocuments,"MS002_Runtime_"+user.Value);
        EnsurePrivateDirectory(parent,user);

        var manifest=Path.Combine(bundledRuntime,RuntimeIntegrityCheck.ManifestName);
        var version=Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(manifest)))[..20];
        var staged=Path.Combine(parent,version);

        if(Directory.Exists(staged))
        {
            if(new DirectoryInfo(staged).LinkTarget is not null)
                throw new IOException("Java 运行库缓存目录不能是符号链接。");
            var saved=RuntimeIntegrityCheck.Verify(staged);
            if(saved.Healthy && File.ReadAllBytes(manifest).AsSpan().SequenceEqual(
                File.ReadAllBytes(Path.Combine(staged,RuntimeIntegrityCheck.ManifestName))))
                return staged;
        }

        // Existing stage directories are re-owned/restricted before copying
        // and are never used if any file fails verification. The source was
        // checked before the first byte was copied.
        EnsurePrivateDirectory(staged,user);
        foreach(var source in Directory.EnumerateFiles(bundledRuntime,"*",SearchOption.AllDirectories))
        {
            var relative=Path.GetRelativePath(bundledRuntime,source);
            if(relative.StartsWith("..",StringComparison.Ordinal) ||
                Path.IsPathRooted(relative))
                throw new IOException("运行库包含无效源文件路径。");
            if((File.GetAttributes(source)&FileAttributes.ReparsePoint)!=0)
                throw new IOException("运行库中发现不允许的符号链接。");
            var dest=Path.Combine(staged,relative);
            Directory.CreateDirectory(Path.GetDirectoryName(dest)!);
            File.Copy(source,dest,true);
        }

        var check=RuntimeIntegrityCheck.Verify(staged);
        if(!check.Healthy || !File.ReadAllBytes(manifest).AsSpan().SequenceEqual(
           File.ReadAllBytes(Path.Combine(staged,RuntimeIntegrityCheck.ManifestName))))
            throw new IOException("兼容 Java 运行库复制后校验失败："+check.Detail);
        return staged;
    }

    internal static bool IsAscii(string path)=>path.All(c=>c<=127);

    static void EnsurePrivateDirectory(string path,SecurityIdentifier user)
    {
        var dir=new DirectoryInfo(path);
        if(dir.Exists)
        {
            if(dir.LinkTarget is not null)
                throw new IOException("运行库目录是链接，拒绝执行。");
            var current=dir.GetAccessControl(AccessControlSections.Owner);
            var owner=(SecurityIdentifier)current.GetOwner(typeof(SecurityIdentifier));
            if(owner!=user)
                throw new UnauthorizedAccessException("运行库目录不属于当前 Windows 用户。");
        }
        var acl=new DirectorySecurity();
        acl.SetAccessRuleProtection(true,false);
        acl.SetOwner(user);
        var inheritance=InheritanceFlags.ContainerInherit|InheritanceFlags.ObjectInherit;
        acl.AddAccessRule(new FileSystemAccessRule(
            user,FileSystemRights.FullControl,inheritance,
            PropagationFlags.None,AccessControlType.Allow));
        acl.AddAccessRule(new FileSystemAccessRule(
            new SecurityIdentifier(WellKnownSidType.LocalSystemSid,null),
            FileSystemRights.FullControl,inheritance,
            PropagationFlags.None,AccessControlType.Allow));
        acl.AddAccessRule(new FileSystemAccessRule(
            new SecurityIdentifier(WellKnownSidType.BuiltinAdministratorsSid,null),
            FileSystemRights.FullControl,inheritance,
            PropagationFlags.None,AccessControlType.Allow));
        if(dir.Exists)
            dir.SetAccessControl(acl);
        else
            dir.Create(acl);
    }
}
