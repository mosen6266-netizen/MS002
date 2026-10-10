using System.IO.Compression;
using System.Text.Json;
using SignalScheduler.Shared;
using Xunit;

namespace SignalScheduler.Migration.Tests;

public sealed class PortableAccountMappingTests
{
    static ManagedAccount Account(string id,string label)=>
        new(id,label,true,true,1);

    static ScriptSaveRequest Script()=>new(
        "source-id","跨电脑完整剧本","source-computer-group-id",7,
        new[]{
            new ScriptEditorStep(0,"+491111","第一句","",false,"",15,5),
            new ScriptEditorStep(1,"+492222","第二句","",true,"请人工确认后继续",65,9),
            new ScriptEditorStep(2,"","默认成员消息","",false,"",6,4)
        });

    [Fact]
    public void MatchingRemarksMapAcrossDifferentAccountIdentifiersAndKeepEverySetting()
    {
        var source=new[]{Account("+491111","角色A"),Account("+492222","角色B")};
        var portable=PortableAccountMapping.ToRemarks(new[]{Script()},source);
        Assert.Equal("角色A",portable[0].Steps[0].Account);
        Assert.Equal("角色B",portable[0].Steps[1].Account);
        Assert.Equal("",portable[0].Steps[2].Account);
        Assert.Equal("",portable[0].TargetGroupId);
        var restored=PortableAccountMapping.ResolveRemarks(portable,new[]{
            Account("+331234","角色B"),Account("+441234","角色A")
        });
        Assert.Equal("+441234",restored[0].Steps[0].Account);
        Assert.Equal("+331234",restored[0].Steps[1].Account);
        Assert.Equal(Script().Steps[1].ReminderText,restored[0].Steps[1].ReminderText);
        Assert.True(restored[0].Steps[1].PauseAfter);
        Assert.Equal(65,restored[0].Steps[1].DelayAfter);
        Assert.Equal(9,restored[0].Steps[1].TypingSeconds);
        Assert.Null(restored[0].ScriptId);
        Assert.Equal("",restored[0].TargetGroupId);
    }

    [Fact]
    public void MissingRemarkPreventsImportButDuplicateRemarkIsDeferredToGroupResolution()
    {
        var portable=PortableAccountMapping.ToRemarks(new[]{Script()},
            new[]{Account("+491111","角色A"),Account("+492222","角色B")});
        Assert.Throws<InvalidDataException>(()=>PortableAccountMapping.ResolveRemarks(
            portable,new[]{Account("other","角色A")}));
        var withDuplicates=PortableAccountMapping.ResolveRemarks(
            portable,new[]{Account("x","角色A"),Account("y","角色A"),
                Account("z","角色B")});
        Assert.Equal("x",withDuplicates[0].Steps[0].Account);
        Assert.Equal("z",withDuplicates[0].Steps[1].Account);
    }

    [Fact]
    public void ExportRejectsUnlabeledButAllowsRepeatedSourceRemarks()
    {
        Assert.Throws<InvalidDataException>(()=>PortableAccountMapping.ToRemarks(
            new[]{Script()},new[]{Account("+491111","+491111"),
                Account("+492222","角色B")}));
        var shared=PortableAccountMapping.ToRemarks(
            new[]{Script()},new[]{Account("+491111","角色A"),
                Account("+492222","角色A")});
        Assert.Equal("角色A",shared[0].Steps[0].Account);
        Assert.Equal("角色A",shared[0].Steps[1].Account);
    }

    [Fact]
    public void ZipManifestContainsOnlyRemarksAndNeverSourceAccountOrGroupIds()
    {
        var root=Path.Combine(Path.GetTempPath(),"ms002-portable-"+Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            var portable=PortableAccountMapping.ToRemarks(new[]{Script()},
                new[]{Account("+491111","角色A"),Account("+492222","角色B")});
            var zip=Path.Combine(root,"portable.zip");
            ScriptBundleArchive.Create(zip,portable,
                new Dictionary<string,string>());
            using(var archive=ZipFile.OpenRead(zip))
            using(var reader=new StreamReader(archive.GetEntry("manifest.json")!.Open()))
            {
                var json=reader.ReadToEnd();
                using var document=JsonDocument.Parse(json);
                var actualSteps=document.RootElement.GetProperty("Scripts")[0]
                    .GetProperty("Steps");
                Assert.Equal("角色A",actualSteps[0].GetProperty("Account").GetString());
                Assert.Equal("角色B",actualSteps[1].GetProperty("Account").GetString());
                Assert.Equal("请人工确认后继续",
                    actualSteps[1].GetProperty("ReminderText").GetString());
                Assert.DoesNotContain("+491111",json);
                Assert.DoesNotContain("+492222",json);
                Assert.DoesNotContain("source-computer-group-id",json);
                Assert.DoesNotContain("source-id",json);
                Assert.Equal(2,document.RootElement.GetProperty("Version").GetInt32());
            }
        }
        finally{Directory.Delete(root,true);}
    }
}
