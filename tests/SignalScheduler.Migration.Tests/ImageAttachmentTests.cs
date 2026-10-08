using SignalScheduler.Engine;
using SignalScheduler.Engine.Persistence;
using SignalScheduler.Shared;
using Xunit;

namespace SignalScheduler.Migration.Tests;

public sealed class ImageAttachmentTests
{
    static readonly CancellationToken Ct=CancellationToken.None;
    static readonly byte[] Png=Convert.FromBase64String(
        "iVBORw0KGgoAAAANSUhEUgAAAAEAAAABCAQAAAC1HAwCAAAAC0lEQVR42mP8/x8AAwMCAO+/g2QAAAAASUVORK5CYII=");

    [Fact]
    public async Task ImportedPictureIsContentAddressedAndSurvivesUpgradeAndRestart()
    {
        var (store,root)=await NewAsync();
        var original=Path.Combine(root,"头像.png");
        await File.WriteAllBytesAsync(original,Png,Ct);

        var receipt=await store.ImportImageAsync(new ImageImportRequest(original),Ct);
        Assert.StartsWith("img:",receipt.Reference);
        Assert.EndsWith(".png",receipt.Reference);
        Assert.True(File.Exists(receipt.AbsolutePath));
        Assert.True(receipt.AbsolutePath.StartsWith(root,StringComparison.OrdinalIgnoreCase));
        Assert.Equal(Png,await File.ReadAllBytesAsync(receipt.AbsolutePath,Ct));

        var same=await store.ImportImageAsync(new ImageImportRequest(original),Ct);
        Assert.Equal(receipt.Reference,same.Reference);
        Assert.Equal(receipt.AbsolutePath,same.AbsolutePath);

        // The original file is no longer needed after import.
        File.Delete(original);
        var reopened=new StateStore(RuntimePaths.ForTesting(root,
            Path.Combine(root,"data.db")));
        await reopened.InitializeAsync(Ct);
        var resolved=await reopened.LookupImageAsync(
            new ImageLookupRequest(receipt.Reference),Ct);
        Assert.Equal(receipt.Sha256,resolved.Sha256);
        Assert.Equal(Png,await File.ReadAllBytesAsync(resolved.AbsolutePath,Ct));
    }

    [Fact]
    public async Task RejectsForgedReferencesAndFilesDisguisedAsPictures()
    {
        var (store,root)=await NewAsync();
        var fake=Path.Combine(root,"fake.jpg");
        await File.WriteAllTextAsync(fake,"not an image",Ct);
        await Assert.ThrowsAsync<ArgumentException>(()=>
            store.ImportImageAsync(new ImageImportRequest(fake),Ct));
        await Assert.ThrowsAsync<ArgumentException>(()=>
            store.LookupImageAsync(new ImageLookupRequest(
                "img:../secrets.txt"),Ct));
        await Assert.ThrowsAsync<ArgumentException>(()=>
            store.LookupImageAsync(new ImageLookupRequest(
                @"C:\sensitive\photo.png"),Ct));
        await Assert.ThrowsAsync<ArgumentException>(()=>
            store.LookupImageAsync(new ImageLookupRequest("img:123.jpg"),Ct));
    }

    [Fact]
    public async Task TamperingWithStoredBinaryIsDetectedBeforePreview()
    {
        var (store,root)=await NewAsync();
        var source=Path.Combine(root,"photo.png");
        await File.WriteAllBytesAsync(source,Png,Ct);
        var receipt=await store.ImportImageAsync(new ImageImportRequest(source),Ct);
        await File.WriteAllBytesAsync(receipt.AbsolutePath,
            new byte[]{0x89,0x50,0x4e,0x47,0x00},Ct);

        await Assert.ThrowsAsync<IOException>(()=>
            store.LookupImageAsync(new ImageLookupRequest(receipt.Reference),Ct));
        await Assert.ThrowsAsync<IOException>(()=>
            store.ImportImageAsync(new ImageImportRequest(source),Ct));
    }

    [Fact]
    public async Task OversizedImageIsRejectedWithoutWritingIntoAttachmentStore()
    {
        var (store,root)=await NewAsync();
        var path=Path.Combine(root,"large.png");
        await using(var stream=new FileStream(path,FileMode.CreateNew))
            stream.SetLength(16L*1024*1024);
        await Assert.ThrowsAsync<ArgumentException>(()=>
            store.ImportImageAsync(new ImageImportRequest(path),Ct));
        var attachmentRoot=Path.Combine(root,"attachments","images");
        Assert.False(Directory.Exists(attachmentRoot));
    }

    static async Task<(StateStore,string Root)> NewAsync()
    {
        var root=Path.Combine(Path.GetTempPath(),"SignalV8ImageTests",
            Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        var paths=RuntimePaths.ForTesting(root,Path.Combine(root,"data.db"));
        var store=new StateStore(paths);
        await store.InitializeAsync(Ct);
        return (store,root);
    }
}
