using System.Globalization;
using PhotoEditor.Core.Adjustments;
using PhotoEditor.Core.Editing;
using PhotoEditor.Core.Masks;
using SkiaSharp;

namespace PhotoEditor.Core.Presets;

/// <summary>Finds subject / people / sky in a photo (AI); returns null when the kind is not supported.</summary>
public interface IMaskDetector
{
    MaskComponent? Detect(MaskSource source, SKBitmap image);
}

/// <summary>The edit after applying a preset, and a line per step saying what was done.</summary>
public sealed record PresetResult(EditState State, IReadOnlyList<string> Log);

/// <summary>Outcome of applying a preset to a file: <see cref="Error"/> is null on success.</summary>
public sealed record PresetFileResult(string ImagePath, string? Error, IReadOnlyList<string> Log);

/// <summary>Runs <see cref="Preset"/> recipes on a photo.</summary>
public static class PresetEngine
{
    /// <summary>Long side of the working copy used to measure goals.</summary>
    public const int ProbeSize = 400;

    /// <summary>
    /// Applies <paramref name="preset"/> to <paramref name="start"/> for the photo <paramref name="image"/> (full
    /// resolution, unedited). <paramref name="detector"/> provides AI masks; without it those steps are skipped.
    /// </summary>
    public static PresetResult Apply(Preset preset, EditState start, SKBitmap image, IMaskDetector? detector = null)
    {
        var log = new List<string>();
        using var probe = new PhotoProbe(image);
        var state = start;
        foreach (var step in preset.Steps)
        {
            state = step switch
            {
                AutoStep => ApplyAuto(state, image, log),
                SetValuesStep s => ApplySetValues(state, s, log),
                AddMaskStep m => ApplyAddMask(state, m, image, detector, log),
                GoalStep g => ApplyGoal(state, g, probe, log),
                CurvesStep c => state with
                {
                    Adjustments = state.Adjustments with
                    {
                        Curve = (c.Curve ?? PointCurve.Linear).Normalized(), CurveRed = (c.CurveRed ?? PointCurve.Linear).Normalized(),
                        CurveGreen = (c.CurveGreen ?? PointCurve.Linear).Normalized(), CurveBlue = (c.CurveBlue ?? PointCurve.Linear).Normalized(),
                    },
                },
                _ => state,
            };
        }
        return new PresetResult(state, log);
    }

    /// <summary>
    /// Applies a preset to a photo that is not open: decodes it, reads its current edit from the sidecars,
    /// runs the preset and writes the sidecars back (the photo file is not changed).
    /// </summary>
    public static PresetFileResult ApplyToFile(Preset preset, string imagePath, IMaskDetector? detector = null)
    {
        try
        {
            using var image = Imaging.ImageLoader.Load(imagePath);
            // The decoded (upright) size is authoritative; the orientation comes from the file.
            var geometry = EditStore.ReadGeometry(imagePath) with { Width = image.Width, Height = image.Height };
            var (current, _) = EditStore.Load(imagePath, geometry);
            var result = Apply(preset, current, image, detector);
            EditStore.Save(imagePath, result.State, geometry);
            return new PresetFileResult(imagePath, null, result.Log);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidDataException
            or System.Text.Json.JsonException or FormatException)
        {
            return new PresetFileResult(imagePath, ex.Message, []);
        }
    }

    /// <summary>Measures <paramref name="metric"/> of <paramref name="region"/> after <paramref name="state"/> (null = empty region).</summary>
    public static double? Measure(SKBitmap image, EditState state, Region region, GoalMetric metric)
    {
        using var probe = new PhotoProbe(image);
        return probe.Measure(state, region, metric);
    }

    private static EditState ApplyAuto(EditState state, SKBitmap image, List<string> log)
    {
        log.Add("Auto on the whole image");
        return state with { Adjustments = AutoAdjust.Suggest(image, state.Crop, state.Adjustments) };
    }

    private static EditState ApplySetValues(EditState state, SetValuesStep step, List<string> log)
    {
        if (step.Mask is { } name && FindMask(state, name) is null)
        {
            log.Add($"Set values on \"{name}\": mask not found, skipped");
            return state;
        }
        var adjustments = step.Mask is { } n ? FindMask(state, n)!.Adjustments : state.Adjustments;
        var changed = new List<string>();
        foreach (var (id, value) in step.Values)
        {
            if (AdjustmentParameters.ById(id) is not { } p)
            {
                changed.Add($"unknown slider \"{id}\" ignored");
                continue;
            }
            adjustments = p.Set(adjustments, step.Relative ? p.Get(adjustments) + value : value);
            changed.Add($"{p.Label} {Format(p, p.Get(adjustments))}");
        }
        log.Add($"Set {(step.Mask is null ? "whole image" : $"\"{step.Mask}\"")}: {string.Join(", ", changed)}");
        return WithAdjustments(state, step.Mask, adjustments);
    }

    private static EditState ApplyAddMask(EditState state, AddMaskStep step, SKBitmap image, IMaskDetector? detector, List<string> log)
    {
        var components = Components(step, image, detector);
        if (components is null)
        {
            log.Add($"Mask \"{step.Name}\": {SourceName(step.Source)} needs the AI models, which are not installed yet — skipped");
            return state;
        }
        var existing = FindMask(state, step.Name);
        var mask = new Mask { Id = existing?.Id ?? Guid.NewGuid(), Name = step.Name, Components = [.. components] };
        log.Add($"Mask \"{step.Name}\": {SourceName(step.Source)}");
        return existing is null
            ? state.AddMask(mask)
            : state.UpdateMask(existing.Id, _ => mask);
    }

    private static IEnumerable<MaskComponent>? Components(AddMaskStep step, SKBitmap image, IMaskDetector? detector) => step.Source switch
    {
        MaskSource.TopGradient => [new LinearGradientComponent { Start = new(0.5f, 0f), End = new(0.5f, 0.5f) }],
        MaskSource.BottomGradient => [new LinearGradientComponent { Start = new(0.5f, 1f), End = new(0.5f, 0.5f) }],
        MaskSource.CenterRadial => [new RadialGradientComponent { Center = new(0.5f, 0.5f), RadiusX = 0.3f, RadiusY = 0.3f, Feather = 0.6f }],
        MaskSource.Geometry => step.Components,
        MaskSource.Background => detector?.Detect(MaskSource.Subject, image) is { } subject ? [subject with { Invert = !subject.Invert }] : null,
        _ => detector?.Detect(step.Source, image) is { } detected ? [detected] : null,
    };

    private static string SourceName(MaskSource source) => source switch
    {
        MaskSource.Subject => "Select Subject",
        MaskSource.People => "Select People",
        MaskSource.Sky => "Select Sky",
        MaskSource.Background => "Select Background",
        MaskSource.TopGradient => "gradient from the top",
        MaskSource.BottomGradient => "gradient from the bottom",
        MaskSource.CenterRadial => "radial gradient in the centre",
        _ => "stored shape",
    };

    private static EditState ApplyGoal(EditState state, GoalStep goal, PhotoProbe probe, List<string> log)
    {
        string title = $"Goal: {goal.Region} {goal.Metric.ToString().ToLowerInvariant()} {RelationText(goal)}";
        var fixMask = goal.FixWholeImage ? null : goal.FixMask ?? FixMaskOf(goal);
        foreach (var name in new[] { goal.Region.Mask, goal.Reference?.Mask, fixMask })
        {
            if (name is not null && FindMask(state, name) is null)
            {
                log.Add($"{title}: mask \"{name}\" not found — skipped");
                return state;
            }
        }
        if (AdjustmentParameters.ById(goal.FixBy) is not { } p)
        {
            log.Add($"{title}: unknown slider \"{goal.FixBy}\" — skipped");
            return state;
        }

        // Signed distance from the goal: > 0 means "too high".
        double? Error(EditState s, out double value, out double desired)
        {
            value = desired = double.NaN;
            if (probe.Measure(s, goal.Region, goal.Metric) is not { } v)
                return null;
            double d;
            if (goal.Reference is { } reference)
            {
                if (probe.Measure(s, reference, goal.Metric) is not { } r)
                    return null;
                d = goal.Metric == GoalMetric.Warmth ? r + goal.Target : r * (1 + goal.Target / 100);
            }
            else
            {
                d = goal.Target;
            }
            value = v;
            desired = d;
            return v - d;
        }

        if (Error(state, out double current, out double wanted) is not { } error)
        {
            log.Add($"{title}: region is empty on this photo — skipped");
            return state;
        }
        bool met = goal.Relation switch
        {
            GoalRelation.AtLeast => error >= -goal.Tolerance,
            GoalRelation.AtMost => error <= goal.Tolerance,
            _ => Math.Abs(error) <= goal.Tolerance,
        };
        if (met)
        {
            log.Add($"{title}: already met ({Num(current)} vs {Num(wanted)})");
            return state;
        }

        var adjustments = fixMask is null ? state.Adjustments : FindMask(state, fixMask)!.Adjustments;
        double v0 = p.Get(adjustments);
        double lo = Math.Max(p.Minimum, v0 - goal.MaxChange), hi = Math.Min(p.Maximum, v0 + goal.MaxChange);
        EditState With(double v) => WithAdjustments(state, fixMask, p.Set(adjustments, v));
        double ErrorAt(double v) => Error(With(v), out _, out _) ?? double.NaN;

        double eLo = ErrorAt(lo), eHi = ErrorAt(hi);
        double best;
        bool limited = false;
        if (double.IsNaN(eLo) || double.IsNaN(eHi) || Math.Sign(eLo) == Math.Sign(eHi))
        {
            // Out of reach within the allowed change: go as far as allowed in the better direction.
            best = Math.Abs(eLo) < Math.Abs(eHi) || double.IsNaN(eHi) ? lo : hi;
            limited = true;
        }
        else
        {
            double a = lo, b = hi, ea = eLo;
            for (int i = 0; i < 16; i++)
            {
                double m = (a + b) / 2, em = ErrorAt(m);
                if (Math.Sign(em) == Math.Sign(ea)) { a = m; ea = em; } else { b = m; }
            }
            best = (a + b) / 2;
        }

        var result = With(best);
        Error(result, out double after, out double wantedAfter);
        string where = fixMask is null ? "whole image" : $"\"{fixMask}\"";
        log.Add($"{title}: was {Num(current)} vs {Num(wanted)} → {p.Label} of {where} {Format(p, v0)} → {Format(p, p.Get(fixMask is null ? result.Adjustments : FindMask(result, fixMask)!.Adjustments))}"
            + $" (now {Num(after)} vs {Num(wantedAfter)}{(limited ? ", limited by the maximum change" : "")})");
        return result;
    }

    /// <summary>By default a goal corrects its own region's mask (the whole image for "whole image" / "outside").</summary>
    private static string? FixMaskOf(GoalStep goal) => goal.Region.Outside ? null : goal.Region.Mask;

    private static string RelationText(GoalStep goal)
    {
        string rel = goal.Relation switch { GoalRelation.AtLeast => "≥", GoalRelation.AtMost => "≤", _ => "≈" };
        if (goal.Reference is not { } r)
            return $"{rel} {Num(goal.Target)}";
        string unit = goal.Metric == GoalMetric.Warmth ? "" : "%";
        return $"{rel} {r} {goal.Target.ToString("+0.#;-0.#;+0", CultureInfo.InvariantCulture)}{unit}";
    }

    private static string Num(double v) => v.ToString("0.#", CultureInfo.InvariantCulture);

    private static string Format(AdjustmentParameter p, double v) => v.ToString(p.Format, CultureInfo.InvariantCulture);

    private static Mask? FindMask(EditState state, string name) => state.Masks.Find(m => m.Name == name);

    private static EditState WithAdjustments(EditState state, string? mask, AdjustmentSettings adjustments) =>
        mask is null
            ? state with { Adjustments = adjustments }
            : state.UpdateMask(FindMask(state, mask)!.Id, m => m with { Adjustments = adjustments });

    /// <summary>A small copy of the photo on which regions are rendered and measured.</summary>
    private sealed class PhotoProbe : IDisposable
    {
        private readonly SKBitmap _small;

        public PhotoProbe(SKBitmap image)
        {
            var (w, h) = Imaging.PreviewImage.PreviewSize(image.Width, image.Height, ProbeSize);
            _small = image.Resize(new SKImageInfo(w, h, SKColorType.Rgba8888, SKAlphaType.Premul),
                new SKSamplingOptions(SKFilterMode.Linear, SKMipmapMode.Linear))
                ?? throw new InvalidOperationException("Could not create the preset probe image.");
        }

        public void Dispose() => _small.Dispose();

        /// <summary>The metric over the region inside the crop, or null when the region is (almost) empty.</summary>
        public double? Measure(EditState state, Region region, GoalMetric metric)
        {
            int w = _small.Width, h = _small.Height;
            using var rendered = CpuAdjustmentRenderer.Render(_small, state);
            var weights = Weights(state, region, w, h);
            if (weights is null)
                return null;

            var px = rendered.GetPixelSpan();
            int rowBytes = rendered.RowBytes;
            var values = new List<(float Value, float Weight)>(w * h);
            double total = 0;
            for (int y = 0; y < h; y++)
            for (int x = 0; x < w; x++)
            {
                float wt = weights[y * w + x];
                int o = y * rowBytes + x * 4;
                if (wt <= 0.01f || px[o + 3] == 0)
                    continue;
                float a = px[o + 3] / 255f;
                float r = ColorMath.SrgbToLinear(px[o] / 255f / a);
                float g = ColorMath.SrgbToLinear(px[o + 1] / 255f / a);
                float b = ColorMath.SrgbToLinear(px[o + 2] / 255f / a);
                float value = metric switch
                {
                    GoalMetric.Saturation => SaturationOf(r, g, b),
                    GoalMetric.Warmth => r + b > 1e-4f ? (r - b) / (r + b) * 100 : 0,
                    _ => MathF.Pow(MathF.Max(ToneCurve.Luminance(r, g, b), 0), 1 / ToneCurve.PerceptualGamma) * 100,
                };
                values.Add((value, wt));
                total += wt;
            }
            if (total < 0.01 * w * h)
                return null;

            switch (metric)
            {
                case GoalMetric.Brightness:
                    values.Sort((p, q) => p.Value.CompareTo(q.Value));
                    double half = total / 2, acc = 0;
                    foreach (var (value, wt) in values)
                    {
                        acc += wt;
                        if (acc >= half)
                            return value;
                    }
                    return values[^1].Value;
                case GoalMetric.Contrast:
                    double mean = values.Sum(v => (double)v.Value * v.Weight) / total;
                    double variance = values.Sum(v => (v.Value - mean) * (v.Value - mean) * v.Weight) / total;
                    return Math.Sqrt(variance);
                default:
                    return values.Sum(v => (double)v.Value * v.Weight) / total;
            }
        }

        private static float SaturationOf(float r, float g, float b)
        {
            float max = MathF.Max(r, MathF.Max(g, b)), min = MathF.Min(r, MathF.Min(g, b));
            return max > 1e-4f ? (max - min) / max * 100 : 0;
        }

        /// <summary>Per-pixel weight: region coverage × inside the crop. Null if the mask does not exist.</summary>
        private static float[]? Weights(EditState state, Region region, int w, int h)
        {
            float[] weights;
            if (region.Mask is { } name)
            {
                if (state.Masks.Find(m => m.Name == name) is not { } mask)
                    return null;
                weights = MaskRasterizer.Rasterize(mask, w, h);
                if (region.Outside)
                {
                    for (int i = 0; i < weights.Length; i++)
                        weights[i] = 1 - weights[i];
                }
            }
            else
            {
                weights = new float[w * h];
                Array.Fill(weights, 1f);
            }
            if (!state.Crop.IsDefault)
            {
                var f = state.Crop.Frame(w, h);
                for (int y = 0; y < h; y++)
                for (int x = 0; x < w; x++)
                {
                    var (u, v) = f.ToLocal(x + 0.5, y + 0.5);
                    if (Math.Abs(u) > f.HalfWidth || Math.Abs(v) > f.HalfHeight)
                        weights[y * w + x] = 0;
                }
            }
            return weights;
        }
    }
}
