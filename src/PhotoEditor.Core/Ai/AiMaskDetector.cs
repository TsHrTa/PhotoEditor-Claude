using System.Runtime.CompilerServices;
using PhotoEditor.Core.Masks;
using PhotoEditor.Core.Presets;
using SkiaSharp;

namespace PhotoEditor.Core.Ai;

/// <summary>
/// Detects subject (and later sky / people) masks with AI models from a <see cref="ModelStore"/>. Models are
/// downloaded on first use and loaded once; results are cached per photo bitmap. Thread-safe; call it from
/// a background thread (downloads and inference block).
/// </summary>
public sealed class AiMaskDetector(ModelStore store, Action<string>? report = null) : IMaskDetector, IDisposable
{
    /// <summary>BiRefNet input size.</summary>
    public const int SubjectInputSize = 1024;

    private readonly object _lock = new();
    private OnnxModel? _subject;
    private readonly ConditionalWeakTable<SKBitmap, RasterMaskComponent> _subjects = new();

    public MaskComponent? Detect(MaskSource source, SKBitmap image) => source switch
    {
        MaskSource.Subject => DetectSubject(image),
        _ => null,
    };

    /// <summary>The main subject of the photo as a soft raster mask (long side <see cref="SegmentAnything.MaskSize"/>).</summary>
    public RasterMaskComponent DetectSubject(SKBitmap image)
    {
        lock (_lock)
        {
            if (_subjects.TryGetValue(image, out var cached))
                return cached;
            var model = _subject ??= LoadModel(ModelCatalog.SelectSubject, "Select Subject");
            report?.Invoke("Finding the subject…");
            var output = model.Run(new Dictionary<string, Tensor>
            {
                [model.InputNames[0]] = ImageTensor.Normalized(image, SubjectInputSize, SubjectInputSize),
            })[model.OutputNames[0]];
            int oh = (int)output.Shape[2], ow = (int)output.Shape[3];
            var (w, h) = Imaging.PreviewImage.PreviewSize(image.Width, image.Height, SegmentAnything.MaskSize);
            var coverage = ImageTensor.LogitsToCoverage(output.Data, ow, oh, w, h);
            var result = new RasterMaskComponent { Source = "subject", MaskPng = RasterMaskComponent.EncodePng(coverage, w, h) };
            _subjects.AddOrUpdate(image, result);
            return result;
        }
    }

    /// <summary>Downloads (if needed, with progress through <c>report</c>) and loads a single-file model.</summary>
    private OnnxModel LoadModel(IReadOnlyList<ModelInfo> files, string name)
    {
        if (!files.All(store.IsAvailable))
        {
            long total = files.Where(f => !store.IsAvailable(f)).Sum(f => f.SizeBytes);
            report?.Invoke($"Downloading the {name} model ({total / 1_000_000} MB, only the first time)…");
            var progress = new Relay(p => report?.Invoke($"Downloading the {name} model… {p.Fraction ?? 0:P0} of {total / 1_000_000} MB"));
            store.GetAllAsync(files, progress).GetAwaiter().GetResult();
        }
        report?.Invoke($"Loading the {name} model…");
        var model = OnnxModel.Load(store.PathOf(files[0]));
        report?.Invoke(model.Device == InferenceDevice.DirectML
            ? $"{name} runs on the GPU (DirectML)."
            : $"{name} runs on the CPU ({model.FallbackReason ?? "requested"}); this can take a while.");
        return model;
    }

    private sealed class Relay(Action<DownloadProgress> action) : IProgress<DownloadProgress>
    {
        public void Report(DownloadProgress value) => action(value);
    }

    public void Dispose()
    {
        lock (_lock)
        {
            _subject?.Dispose();
            _subject = null;
        }
    }
}
