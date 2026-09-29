using System.Collections.Immutable;
using PhotoEditor.Core.Adjustments;
using PhotoEditor.Core.Editing;
using PhotoEditor.Core.Export;
using PhotoEditor.Core.Masks;
using PhotoEditor.Core.Presets;
using SkiaSharp;

namespace PhotoEditor.Tests.Presets;

public sealed class PresetEngineTests : IDisposable
{
    private readonly string _dir = Directory.CreateTempSubdirectory("pe-presets-").FullName;

    public void Dispose() => Directory.Delete(_dir, recursive: true);

    /// <summary>Grey background (perceived <paramref name="background"/>) with a centred disc (perceived <paramref name="subject"/>).</summary>
    private static SKBitmap Portrait(float subject = 0.3f, float background = 0.6f)
    {
        var bmp = new SKBitmap(new SKImageInfo(300, 200, SKColorType.Rgba8888, SKAlphaType.Premul));
        byte Gray(float perceived) =>
            (byte)Math.Round(ColorMath.LinearToSrgb(MathF.Pow(perceived, ToneCurve.PerceptualGamma)) * 255);
        using var canvas = new SKCanvas(bmp);
        canvas.Clear(new SKColor(Gray(background), Gray(background), Gray(background)));
        using var paint = new SKPaint { Color = new SKColor(Gray(subject), Gray(subject), Gray(subject)) };
        canvas.DrawCircle(150, 100, 60, paint);
        return bmp;
    }

    /// <summary>"Detects" the disc as the subject.</summary>
    private sealed class DiscDetector : IMaskDetector
    {
        public MaskComponent? Detect(MaskSource source, SKBitmap image) => source == MaskSource.Subject
            ? new RadialGradientComponent { Center = new(0.5f, 0.5f), RadiusX = 0.2f, RadiusY = 0.2f, Feather = 0.1f }
            : null;
    }

    private static readonly Region Subject = new("Subject");
    private static readonly Region Background = new("Subject", Outside: true);

    private static GoalStep BrighterSubject(double percent, double maxChange = 3) => new()
    {
        Region = Subject, Metric = GoalMetric.Brightness, Relation = GoalRelation.AtLeast,
        Reference = Background, Target = percent, FixBy = "exposure", MaxChange = maxChange,
    };

    private static Preset Recipe(params PresetStep[] steps) => new() { Name = "test", Steps = [.. steps] };

    private static double Brightness(SKBitmap img, EditState s, Region r) =>
        PresetEngine.Measure(img, s, r, GoalMetric.Brightness)!.Value;

    [Fact]
    public void SliderIds_AreUnique_AndFound()
    {
        var ids = AdjustmentParameters.All.Select(p => p.Id).ToList();
        Assert.Equal(ids.Count, ids.Distinct().Count());
        Assert.Same(AdjustmentParameters.Exposure, AdjustmentParameters.ById("exposure"));
        Assert.NotNull(AdjustmentParameters.ById("luminance.blues"));
        Assert.Null(AdjustmentParameters.ById("nope"));
    }

    [Fact]
    public void Goal_DarkSubject_IsBrightenedToTarget()
    {
        using var img = Portrait(subject: 0.3f, background: 0.6f);
        var r = PresetEngine.Apply(Recipe(new AddMaskStep { Name = "Subject" }, BrighterSubject(15)),
            EditState.Default, img, new DiscDetector());
        var mask = Assert.Single(r.State.Masks);
        Assert.True(mask.Adjustments.Exposure > 1, $"exposure {mask.Adjustments.Exposure}");
        Assert.Equal(0, r.State.Adjustments.Exposure); // only the subject changed
        double subject = Brightness(img, r.State, Subject), background = Brightness(img, r.State, Background);
        Assert.InRange(subject / background, 1.13, 1.2);
        Assert.Contains(r.Log, l => l.Contains("Exposure of \"Subject\""));
    }

    [Fact]
    public void Goal_AlreadyMet_ChangesNothing()
    {
        using var img = Portrait(subject: 0.8f, background: 0.4f);
        var r = PresetEngine.Apply(Recipe(new AddMaskStep { Name = "Subject" }, BrighterSubject(15)),
            EditState.Default, img, new DiscDetector());
        Assert.Equal(AdjustmentSettings.Default, r.State.Masks[0].Adjustments);
        Assert.Contains(r.Log, l => l.Contains("already met"));
    }

    [Fact]
    public void Goal_Unreachable_StopsAtMaxChange()
    {
        using var img = Portrait(subject: 0.05f, background: 0.9f);
        var r = PresetEngine.Apply(Recipe(new AddMaskStep { Name = "Subject" }, BrighterSubject(50, maxChange: 0.5)),
            EditState.Default, img, new DiscDetector());
        Assert.Equal(0.5, r.State.Masks[0].Adjustments.Exposure);
        Assert.Contains(r.Log, l => l.Contains("limited"));
    }

    [Fact]
    public void Goal_AtMost_DarkensABrightSkyGradient()
    {
        // Bright top half, darker bottom half.
        var bmp = new SKBitmap(new SKImageInfo(200, 200, SKColorType.Rgba8888, SKAlphaType.Premul));
        using (var canvas = new SKCanvas(bmp))
        {
            canvas.Clear(new SKColor(90, 90, 90));
            using var paint = new SKPaint { Color = new SKColor(235, 235, 235) };
            canvas.DrawRect(0, 0, 200, 100, paint);
        }
        using var img = bmp;
        var sky = new Region("Sky");
        var goal = new GoalStep
        {
            Region = sky, Metric = GoalMetric.Brightness, Relation = GoalRelation.AtMost,
            Reference = Region.WholeImage, Target = 10, FixBy = "exposure", MaxChange = 4,
        };
        var r = PresetEngine.Apply(Recipe(new AddMaskStep { Name = "Sky", Source = MaskSource.TopGradient }, goal), EditState.Default, img);
        Assert.True(r.State.Masks[0].Adjustments.Exposure < -0.2, string.Join("\n", r.Log));
        Assert.True(Brightness(img, r.State, sky) <= Brightness(img, r.State, Region.WholeImage) * 1.1 + 1.5, string.Join("\n", r.Log));
    }

    [Fact]
    public void Background_IsTheInvertedSubject()
    {
        using var img = Portrait(subject: 0.3f, background: 0.6f);
        var r = PresetEngine.Apply(Recipe(new AddMaskStep { Name = "BG", Source = MaskSource.Background }), EditState.Default, img, new DiscDetector());
        Assert.True(r.State.Masks[0].Components[0].Invert);
        Assert.InRange(Brightness(img, r.State, new Region("BG")), 59, 61);
    }

    [Fact]
    public void WithoutAiModels_MaskStepsAndDependentStepsAreSkipped()
    {
        using var img = Portrait();
        var preset = Recipe(
            new AutoStep(),
            new AddMaskStep { Name = "Subject", Source = MaskSource.Subject },
            BrighterSubject(15),
            new SetValuesStep { Mask = "Subject", Values = ImmutableSortedDictionary<string, double>.Empty.Add("shadows", 10) });
        var r = PresetEngine.Apply(preset, EditState.Default, img);
        Assert.Empty(r.State.Masks);
        Assert.NotEqual(AdjustmentSettings.Default, r.State.Adjustments); // Auto still ran
        Assert.Equal(4, r.Log.Count);
        Assert.Contains("needs the AI models", r.Log[1]);
        Assert.Contains("not found", r.Log[2]);
        Assert.Contains("not found", r.Log[3]);
    }

    [Fact]
    public void SetValues_AbsoluteRelativeAndUnknown()
    {
        using var img = Portrait();
        var start = new EditState { Adjustments = new AdjustmentSettings { Contrast = 10, Shadows = 5 } };
        var preset = Recipe(
            new SetValuesStep { Values = ImmutableSortedDictionary<string, double>.Empty.Add("contrast", 30).Add("bogus", 1) },
            new SetValuesStep { Relative = true, Values = ImmutableSortedDictionary<string, double>.Empty.Add("shadows", 20).Add("exposure", 9) });
        var r = PresetEngine.Apply(preset, start, img);
        Assert.Equal(30, r.State.Adjustments.Contrast);
        Assert.Equal(25, r.State.Adjustments.Shadows);
        Assert.Equal(5, r.State.Adjustments.Exposure); // clamped to the slider range
        Assert.Contains("unknown slider", r.Log[0]);
    }

    [Fact]
    public void FromEdit_ReproducesTheEdit()
    {
        using var img = Portrait();
        var edit = new EditState
        {
            Adjustments = new AdjustmentSettings { Exposure = 0.4, Temperature = 12, Blues = new HslBand(-5, 10, 0), SharpenAmount = 40 },
            Masks =
            [
                new Mask
                {
                    Name = "Top", Adjustments = new AdjustmentSettings { Exposure = -0.7, Saturation = 20 },
                    Components = [new LinearGradientComponent { Start = new(0.5f, 0), End = new(0.5f, 0.4f) }],
                },
            ],
        };
        var preset = PresetFactory.FromEdit("mine", edit, SettingsGroups.All);
        var r = PresetEngine.Apply(preset, new EditState { Adjustments = new AdjustmentSettings { Contrast = 50 } }, img);
        Assert.Equal(edit.Adjustments, r.State.Adjustments); // all groups copied, contrast reset to the edit's 0
        var mask = Assert.Single(r.State.Masks);
        Assert.Equal("Top", mask.Name);
        Assert.Equal(edit.Masks[0].Adjustments, mask.Adjustments);
        Assert.Equal(edit.Masks[0].Components, mask.Components);
    }

    [Fact]
    public void Reapplying_ReplacesTheMaskOfTheSameName()
    {
        using var img = Portrait();
        var preset = Recipe(new AddMaskStep { Name = "Sky", Source = MaskSource.TopGradient },
            new SetValuesStep { Mask = "Sky", Relative = true, Values = ImmutableSortedDictionary<string, double>.Empty.Add("exposure", -0.5) });
        var once = PresetEngine.Apply(preset, EditState.Default, img).State;
        var twice = PresetEngine.Apply(preset, once, img).State;
        var mask = Assert.Single(twice.Masks);
        Assert.Equal(-0.5, mask.Adjustments.Exposure); // fresh mask, not -1.0
    }

    [Fact]
    public void Json_RoundTripsBuiltIns()
    {
        foreach (var preset in PresetFactory.BuiltIn)
        {
            var json = PresetStore.Serialize(preset);
            Assert.Contains("\"type\": \"goal\"", json);
            Assert.Equal(preset with { IsBuiltIn = false }, PresetStore.Deserialize(json));
        }
    }

    [Fact]
    public void Store_SavesLoadsAndDeletes()
    {
        var store = new PresetStore(_dir);
        store.Save(PresetFactory.BuiltIn[1] with { Name = "My sky: v2?" });
        File.WriteAllText(Path.Combine(_dir, "broken.json"), "{ nope");
        var all = store.LoadAll(out var errors);
        Assert.Equal("My sky: v2?", Assert.Single(all).Name);
        Assert.Single(errors);
        store.Delete("My sky: v2?");
        Assert.Empty(store.LoadAll(out _));
    }

    [Fact]
    public void ApplyToFile_WritesSidecarsOnly()
    {
        using var img = Portrait();
        var path = Path.Combine(_dir, "p.jpg");
        File.WriteAllBytes(path, ImageExporter.Encode(img, new ExportOptions()));
        var before = File.ReadAllBytes(path);
        var result = PresetEngine.ApplyToFile(PresetFactory.BuiltIn[1], path);
        Assert.Null(result.Error);
        Assert.Equal(before, File.ReadAllBytes(path));
        var saved = SidecarFile.Load(path)!.ToState();
        Assert.Equal("Sky", Assert.Single(saved.Masks).Name);
        Assert.True(File.Exists(LightroomXmp.PathFor(path)));
    }
}
