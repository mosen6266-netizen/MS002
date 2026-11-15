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
            Assert.Equal(6,result.FilesVerified);
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
    public void AdditionalLegacyJarInWildcardDirectoryIsRejected()
    {
        var root=CreateFixture(includeResource:true);
        try
        {
            // Simulates an old jar that Inno Setup's ignoreversion overlay
            // previously left behind, which could shadow current classes.
            var oldJar=Path.Combine(root,"signal-cli","lib","legacy-argparse4j.jar");
            File.Copy(Path.Combine(root,"signal-cli","lib","argparse4j.jar"),oldJar);
            var result=RuntimeIntegrityCheck.Verify(root);
            Assert.False(result.Healthy);
            Assert.Equal("EXTRA_JAR",result.Code);
            Assert.Contains("legacy-argparse4j.jar",result.Detail);
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
        var overlay=Path.Combine(root,"signal-cli","resource-overlay");
        var resDir=Path.Combine(overlay,"net","sourceforge","argparse4j","internal");
        Directory.CreateDirectory(resDir);
        File.WriteAllText(Path.Combine(resDir,"ArgumentParserImpl.properties"),"usage=usage");
        File.WriteAllText(Path.Combine(resDir,"ArgumentParserImpl_en.properties"),"usage=usage");
        File.WriteAllBytes(Path.Combine(overlay,"MS002ResourceBundleProbe.class"),new byte[]{1,2,3});
        var entries=new[]{"jre/bin/java.exe",
            "signal-cli/lib/argparse4j.jar","signal-cli/lib/signal.jar",
            "signal-cli/resource-overlay/net/sourceforge/argparse4j/internal/ArgumentParserImpl.properties",
            "signal-cli/resource-overlay/net/sourceforge/argparse4j/internal/ArgumentParserImpl_en.properties",
            "signal-cli/resource-overlay/MS002ResourceBundleProbe.class"}
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
