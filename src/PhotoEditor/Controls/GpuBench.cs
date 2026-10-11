using System.Linq;
using System;
using System.Collections.Generic;
using System.Diagnostics;
using PhotoEditor.Core.Adjustments;
using PhotoEditor.Core.Editing;
using PhotoEditor.Core.Imaging;
using PhotoEditor.ViewModels;
using SkiaSharp;

namespace PhotoEditor.Controls;

/// <summary>
/// GPU cost of the adjustment shader on this PC, measured once per session when the environment variable
/// PHOTOEDITOR_GPU_BENCH is set and a fully decoded photo of at least 20 MP is shown (the quick half-size preview is
/// skipped): a 1920 × 1080 offscreen surface on the viewer's own
/// GPU context, drawn at fit size and at 100 % for a few settings; 20 draws per synchronous flush (one flush costs tens of
/// ms on its own), the median of 5 such batches per draw goes to timings.log.
/// </summary>
internal static class GpuBench
{
    private static bool _done;

    public static bool Wanted(PreviewImage source) =>
        !_done && !source.IsStandIn && (long)source.Width * source.Height >= 20_000_000
        && Environment.GetEnvironmentVariable("PHOTOEDITOR_GPU_BENCH") is not null;

    public static void Run(GRContext gpu, PreviewImage source)
    {
        _done = true;
        const int width = 1920, height = 1080;
        using var surface = SKSurface.Create(gpu, false, new SKImageInfo(width, height, SKColorType.Rgba8888, SKAlphaType.Premul));
        if (surface is null)
            return;
        var baseline = AdjustmentSettings.RawDefault with { SharpenDetail = 0 };
        var cases = new (string Name, AdjustmentSettings Settings)[]
        {
            ("nothing", AdjustmentSettings.Default),
            ("sharpening 40 alone", AdjustmentSettings.Default with { SharpenAmount = 40 }),
            ("colour NR 25 alone", AdjustmentSettings.Default with { NoiseColor = 25 }),
            ("luminance NR 30 alone", AdjustmentSettings.Default with { NoiseLuminance = 30 }),
            ("sharpening 40", baseline),
            ("+ Detail 25", baseline with { SharpenDetail = 25 }),
            ("+ Detail 25, luminance NR 30", baseline with { SharpenDetail = 25, NoiseLuminance = 30 }),
            ("+ Detail 25, Highlights -50", baseline with { SharpenDetail = 25, Highlights = -50 }),
        };
        double fit = Math.Min((double)width / source.Width, (double)height / source.Height);
        foreach (var (view, scale) in new[] { ("fit", fit), ("100 %", 1.0) })
        {
            var image = source.ImageFor(scale);
            double toImage = scale * source.Width / image.Width;
            var toView = SKMatrix.CreateTranslation(width / 2f, height / 2f)
                .PreConcat(SKMatrix.CreateScale((float)toImage, (float)toImage))
                .PreConcat(SKMatrix.CreateTranslation(-image.Width / 2f, -image.Height / 2f));
            var sampling = new SKSamplingOptions(SKFilterMode.Linear, SKMipmapMode.None);
            foreach (var (name, settings) in cases.Prepend(("plain image shader (control)", (AdjustmentSettings)null!)))
            {
                using var shader = settings is null
                    ? image.ToShader(SKShaderTileMode.Clamp, SKShaderTileMode.Clamp, sampling)
                    : AdjustmentShader.CreateShader(image, new EditState { Adjustments = settings }, sampling,
                        _ => null, (double)image.Width / source.Width);
                using var paint = new SKPaint { Shader = shader };
                const int perBatch = 20;
                var times = new List<double>();
                var canvas = surface.Canvas;
                for (int batch = 0; batch < 6; batch++)
                {
                    canvas.Clear(SKColors.Black);
                    gpu.Flush(true, true);
                    var watch = Stopwatch.StartNew();
                    for (int i = 0; i < perBatch; i++)
                    {
                        canvas.Save();
                        canvas.Concat(in toView);
                        canvas.DrawRect(0, 0, image.Width, image.Height, paint);
                        canvas.Restore();
                    }
                    gpu.Flush(true, true);
                    if (batch > 0)
                        times.Add(watch.Elapsed.TotalMilliseconds / perBatch);
                }
                times.Sort();
                Timings.Log($"GPU bench {view} ({image.Width} × {image.Height} drawn into {width} × {height}): {name}: {times[times.Count / 2]:0.0} ms");
            }
        }
    }
}
