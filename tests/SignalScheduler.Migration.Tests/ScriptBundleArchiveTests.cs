using System.IO.Compression;
using System.Security.Cryptography;
using System.Text.Json;
using SignalScheduler.Shared;
using Xunit;

namespace SignalScheduler.Migration.Tests;

public sealed class ScriptBundleArchiveTests
{
    static string Temp()
    {
        var p=Path.Combine(Path.GetTempPath(),"ms002-test-"+Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(p);
        return p;
    }
    static ScriptSaveRequest Script(string attachment)=>new(
        null,"可移植剧本","",0,
        new[]{new ScriptEditorStep(0,"","示例内容",attachment,false,"",5,5)});

    [Fact]
    public void MultiScriptBackupRoundTrip_ValidatesImageBytesAndPortableReferences()
    {
        var root=Temp();
        try
        {
            var png=Path.Combine(root,"picture.png");
            var bytes=new byte[]{137,80,78,71,13,10,26,10,1,2,3,4};
            File.WriteAllBytes(png,bytes);
            var bundle=Path.Combine(root,"backup.zip");
            var reference="img:original";
            ScriptBundleArchive.Create(bundle,
                new[]{Script(reference),Script(reference)},
                new Dictionary<string,string>{{reference,png}});
            var contents=ScriptBundleArchive.ExtractValidated(bundle,
                Path.Combine(root,"staging"));
            Assert.Equal(2,contents.Scripts.Length);
            Assert.All(contents.Scripts,s=>Assert.Null(s.ScriptId));
            Assert.Equal(2,contents.Version);
            Assert.Single(contents.ImportedImagePaths);
            var portableReference=contents.Scripts[0].Steps[0].Attachment;
            Assert.StartsWith("image-",portableReference);
            Assert.Equal(bytes,File.ReadAllBytes(contents.ImportedImagePaths[portableReference]));
            using(var zip=ZipFile.OpenRead(bundle))
            using(var reader=new StreamReader(zip.GetEntry("manifest.json")!.Open()))
            {
                var manifest=reader.ReadToEnd();
                Assert.DoesNotContain("img:original",manifest);
                Assert.DoesNotContain(png,manifest);
            }
        }
        finally{Directory.Delete(root,true);}
    }

    [Fact]
    public void TamperedImage_IsRejectedBeforeScriptImport()
    {
        var root=Temp();
        try
        {
            var png=Path.Combine(root,"picture.png");
            File.WriteAllBytes(png,new byte[]{137,80,78,71,13,10,26,10,1});
            var zipPath=Path.Combine(root,"backup.zip");
            var reference="img:original";
            ScriptBundleArchive.Create(zipPath,new[]{Script(reference)},
                new Dictionary<string,string>{{reference,png}});
            using(var zip=ZipFile.Open(zipPath,ZipArchiveMode.Update))
            {
                var entry=zip.Entries.Single(x=>x.FullName.StartsWith("images/"));
                var name=entry.FullName;
                entry.Delete();
                using var bad=zip.CreateEntry(name).Open();
                bad.Write(new byte[]{1,2,3,4,5});
            }
            Assert.Throws<InvalidDataException>(()=>
                ScriptBundleArchive.ExtractValidated(zipPath,
                    Path.Combine(root,"staging")));
        }
        finally{Directory.Delete(root,true);}
    }

    [Fact]
    public void MaliciousArchiveImagePath_IsNeverExtracted()
    {
        var root=Temp();
        try
        {
            var zipPath=Path.Combine(root,"backup.zip");
            using(var zip=ZipFile.Open(zipPath,ZipArchiveMode.Create))
            {
                var manifest=zip.CreateEntry("manifest.json");
                using var stream=manifest.Open();
                var bytes=JsonSerializer.SerializeToUtf8Bytes(new
                {
                    Version=1,
                    Scripts=new[]{Script("img:original")},
                    Images=new Dictionary<string,string>{{"img:original","../escape.png"}}
                });
                stream.Write(bytes);
            }
            Assert.Throws<InvalidDataException>(()=>
                ScriptBundleArchive.ExtractValidated(zipPath,
                    Path.Combine(root,"staging")));
            Assert.False(File.Exists(Path.Combine(root,"escape.png")));
        }
        finally{Directory.Delete(root,true);}
    }
}
