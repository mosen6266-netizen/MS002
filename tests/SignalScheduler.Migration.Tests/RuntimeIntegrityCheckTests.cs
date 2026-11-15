using System.IO.Compression;
using System.Security.Cryptography;
using System.Text.Json;
using SignalScheduler.Shared;
using Xunit;

namespace SignalScheduler.Migration.Tests;

public sealed class RuntimeIntegrityCheckTests
{
    [Fact]
    public void VerifiedBundleAcceptsCompleteArgparseResources()
    {
        var root=CreateFixture(includeResource:true);
        try
        {
            var result=RuntimeIntegrityCheck.Verify(root);
            Assert.True(result.Healthy,result.Detail);
            Assert.Equal("OK",result.Code);
            Assert.Equal(3,result.FilesVerified);
        }
        finally{Directory.Delete(root,true);}
    }

    [Fact]
    public void RemovedJarIsDetectedBeforeDaemonStartup()
    {
        var root=CreateFixture(includeResource:true);
        try
        {
            File.Delete(Path.Combine(root,"signal-cli","lib","argparse4j.jar"));
            var result=RuntimeIntegrityCheck.Verify(root);
            Assert.False(result.Healthy);
            Assert.Equal("MISSING_FILE",result.Code);
        }
        finally{Directory.Delete(root,true);}
    }

    [Fact]
    public void TamperedJarFailsSha256EvenIfItsSizeIsUnchanged()
    {
        var root=CreateFixture(includeResource:true);
        try
        {
            var path=Path.Combine(root,"signal-cli","lib","argparse4j.jar");
            var bytes=File.ReadAllBytes(path);
            bytes[bytes.Length/2]^=0x20;
            File.WriteAllBytes(path,bytes);
            var result=RuntimeIntegrityCheck.Verify(root);
            Assert.False(result.Healthy);
            Assert.Equal("CORRUPT_FILE",result.Code);
        }
        finally{Directory.Delete(root,true);}
    }

    [Fact]
    public void JarWithArgparseClassButWithoutResourceIsRejected()
    {
        var root=CreateFixture(includeResource:false);
        try
        {
            var result=RuntimeIntegrityCheck.Verify(root);
            Assert.False(result.Healthy);
            Assert.Equal("MISSING_RESOURCE",result.Code);
        }
        finally{Directory.Delete(root,true);}
    }

    static string CreateFixture(bool includeResource)
    {
        var root=Path.Combine(Path.GetTempPath(),"MS002RuntimeChecks",
            Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(Path.Combine(root,"jre","bin"));
        Directory.CreateDirectory(Path.Combine(root,"signal-cli","lib"));
        File.WriteAllBytes(Path.Combine(root,"jre","bin","java.exe"),new byte[]{1,2,3,4,5});
        var argparse=Path.Combine(root,"signal-cli","lib","argparse4j.jar");
        using(var archive=ZipFile.Open(argparse,ZipArchiveMode.Create))
        {
            archive.CreateEntry("net/sourceforge/argparse4j/internal/ArgumentParserImpl.class");
            if(includeResource)
                archive.CreateEntry("net/sourceforge/argparse4j/internal/ArgumentParserImpl_en.properties");
        }
        using(var archive=ZipFile.Open(
            Path.Combine(root,"signal-cli","lib","signal.jar"),ZipArchiveMode.Create))
        {
            archive.CreateEntry("org/asamk/signal/Main.class");
        }
        var entries=new[]{"jre/bin/java.exe",
            "signal-cli/lib/argparse4j.jar","signal-cli/lib/signal.jar"}
            .Select(relative=>{
                var file=Path.Combine(root,relative.Replace('/',Path.DirectorySeparatorChar));
                return new RuntimeIntegrityCheck.Entry(
                    relative,new FileInfo(file).Length,
                    Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(file))).ToLowerInvariant());
            }).ToArray();
        File.WriteAllText(Path.Combine(root,RuntimeIntegrityCheck.ManifestName),
            JsonSerializer.Serialize(new{version=1,files=entries}));
        return root;
    }
}
