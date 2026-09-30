using PhotoEditor.Core.Imaging;
using PhotoEditor.Core.Masks;
using PhotoEditor.Core.Retouch;
using SkiaSharp;

namespace PhotoEditor.Tests.Retouch;

public sealed class RetouchCanvasTests
{
    [Theory]
    [InlineData(7)]
    [InlineData(11)]
    [InlineData(23)]
    public void Updates_GiveTheSamePixelsAsRetouchingFromScratch(int seed)
    {
        using var photo = TestImages.Varied(160, 120, withAlpha: false);
        var extra = new SKBitmap(new SKImageInfo(160, 120, SKColorType.Rgba8888, SKAlphaType.Opaque));
        for (int y = 0; y < 120; y++)
            for (int x = 0; x < 160; x++)
                extra.SetPixel(x, y, new SKColor((byte)(x * 1.5), (byte)(y * 2), 0));
        Headroom.Attach(photo, new Headroom(extra, 0.4f));
        using var image = SKImage.FromBitmap(photo);
        Headroom.Attach(image, Headroom.Of(photo));
        using var canvas = new RetouchCanvas(image);

        var rnd = new Random(seed);
        BrushPoint P() => new((float)rnd.NextDouble(), (float)rnd.NextDouble());
        var spots = new List<Spot>();
        for (int step = 0; step < 60; step++)
        {
            int action = spots.Count == 0 ? 0 : rnd.Next(5);
            switch (action)
            {
                case 0 or 1: // add
                    spots.Add(new Spot { Center = P(), Source = P(), Radius = 0.02f + (float)rnd.NextDouble() * 0.06f,
                        Mode = rnd.Next(2) == 0 ? SpotMode.Heal : SpotMode.Clone, Feather = (float)rnd.NextDouble() });
                    break;
                case 2: // move a spot
                    int i = rnd.Next(spots.Count);
                    spots[i] = spots[i] with { Center = P() };
                    break;
                case 3: // move a source
                    int j = rnd.Next(spots.Count);
                    spots[j] = spots[j] with { Source = P() };
                    break;
                default: // remove
                    spots.RemoveAt(rnd.Next(spots.Count));
                    break;
            }
            canvas.Update(spots);
            var expected = Retouching.Apply(photo, spots); // the photo itself when there are no spots
            Assert.True(TestImages.MaxDifference(expected, canvas.Bitmap, out var at) == 0, $"step {step}: pixels differ at {at}");
            var expectedExtra = Headroom.Of(expected)?.Bitmap ?? extra;
            Assert.True(TestImages.MaxDifference(expectedExtra, canvas.HeadroomBitmap!, out at) == 0, $"step {step}: headroom differs at {at}");
            if (!ReferenceEquals(expected, photo))
                expected.Dispose();
        }
        // Reordering redoes everything.
        spots.Reverse();
        canvas.Update(spots);
        using var reordered = Retouching.Apply(photo, spots);
        Assert.Equal(0, TestImages.MaxDifference(reordered, canvas.Bitmap, out _));
        Assert.False(canvas.Update(spots));
    }
}
