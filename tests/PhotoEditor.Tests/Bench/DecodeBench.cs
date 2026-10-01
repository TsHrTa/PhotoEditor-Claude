using System.Diagnostics;
using PhotoEditor.Core.Editing;
using PhotoEditor.Core.Imaging;
using Xunit.Abstractions;

namespace PhotoEditor.Tests.Bench;

/// <summary>Timings of the steps of opening a photo; runs only when PHOTOEDITOR_BENCH names a file.</summary>
public sealed class DecodeBench(ITestOutputHelper output)
{
    [Fact]
    public void Measure()
    {
        var path = Environment.GetEnvironmentVariable("PHOTOEDITOR_BENCH");
        if (path is null)
            return;
        var w = Stopwatch.StartNew();
        void Log(string what)
        {
            output.WriteLine($"{what}: {w.ElapsedMilliseconds} ms");
            w.Restart();
        }
        EditStore.ReadGeometry(path);
        Log("geometry (warm-up)");
        for (int i = 0; i < 2; i++)
        {
            w.Restart();
            using var camera = EmbeddedPreview.Load(path);
            Log("embedded preview");
            if (camera is not null)
            {
                PreviewImage.Create(camera);
                Log("preview of the embedded preview");
            }
            using var decoded = ImageLoader.Load(path);
            Log($"decode {decoded.Width} × {decoded.Height}");
            PreviewImage.Create(decoded);
            Log("preview of the decoded photo");
        }
    }
}
