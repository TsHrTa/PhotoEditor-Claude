using System;
using System.Linq;
using System.Net.Http;
using System.Threading.Tasks;
using PhotoEditor.Core.Ai;
using PhotoEditor.Core.Editing;
using PhotoEditor.Core.Masks;
using SkiaSharp;

namespace PhotoEditor.ViewModels;

/// <summary>AI selection (Select Object: click / box with SAM 2.1). Models download on first use.</summary>
public partial class MainViewModel
{
    private static readonly HttpClient Http = new() { Timeout = TimeSpan.FromMinutes(30) };
    private readonly ModelStore _models = new(ModelStore.DefaultDirectory, Http);

    private AiMaskDetector? _detector;

    /// <summary>Subject (and later sky / people) detection; reports progress in the status bar.</summary>
    private AiMaskDetector Detector => _detector ??= new AiMaskDetector(_models,
        message => Avalonia.Threading.Dispatcher.UIThread.Post(() => Status = message));

    /// <summary>Adds the photo's main subject (AI) to the selected mask, or to a new mask "Subject".</summary>
    [CommunityToolkit.Mvvm.Input.RelayCommand]
    private Task SelectSubject() => AddDetectedAsync("Subject", d => d.DetectSubject, "Subject selected. Tick Invert on the component for the background.");

    /// <summary>Adds the sky (AI) to the selected mask, or to a new mask "Sky".</summary>
    [CommunityToolkit.Mvvm.Input.RelayCommand]
    private Task SelectSky() => AddDetectedAsync("Sky", d => d.DetectSky, "Sky selected.");

    /// <summary>Adds all people (AI) to the selected mask, or to a new mask "People".</summary>
    [CommunityToolkit.Mvvm.Input.RelayCommand]
    private Task SelectPeople() => AddDetectedAsync("People", d => d.DetectPeople, "People selected.");

    private async Task AddDetectedAsync(string maskName, Func<AiMaskDetector, Func<SKBitmap, RasterMaskComponent>> detect, string done)
    {
        if (Original is not { } original || EditBase is not { } image || _selecting)
            return;
        _selecting = true;
        try
        {
            var detector = Detector;
            var subject = await Task.Run(() => detect(detector)(image));
            if (!ReferenceEquals(Original, original))
                return;
            Guid targetId;
            if (SelectedMask is { } current)
            {
                targetId = current.Id;
            }
            else
            {
                var created = new Mask { Name = State.Masks.Exists(m => m.Name == maskName) ? State.NextMaskName() : maskName };
                ApplyEdit(State.AddMask(created));
                targetId = created.Id;
            }
            EditMask(targetId, m => m.AddComponent(subject), null);
            if (SelectedMask?.Id != targetId)
                SelectedMask = Masks.FirstOrDefault(m => m.Id == targetId);
            SelectedComponentIndex = State.FindMask(targetId)!.Components.Count - 1;
            Status = done;
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException or System.IO.IOException
            or System.IO.InvalidDataException or Microsoft.ML.OnnxRuntime.OnnxRuntimeException or UnauthorizedAccessException)
        {
            Status = $"Select {maskName} failed: {ex.Message}";
        }
        finally
        {
            _selecting = false;
        }
    }

    private SegmentAnything? _sam;
    private Task<SegmentAnything>? _samLoading;

    /// <summary>Encoder result for <see cref="_embeddingFor"/> (the photo it was computed for).</summary>
    private Task<SamEmbedding>? _embedding;
    private SKBitmap? _embeddingFor;
    private bool _selecting;

    /// <summary>When on, clicks / boxes on the photo select objects (AI).</summary>
    public bool IsObjectSelectActive
    {
        get => ActiveTool == EditTool.ObjectSelect;
        set => SetTool(EditTool.ObjectSelect, value);
    }

    /// <summary>Downloads (first time) and loads the Select Object model.</summary>
    private Task<SegmentAnything> LoadSamAsync()
    {
        if (_sam is not null)
            return Task.FromResult(_sam);
        return _samLoading ??= LoadSamCoreAsync();
    }

    private async Task<SegmentAnything> LoadSamCoreAsync()
    {
        try
        {
            if (!ModelCatalog.SelectObject.All(_models.IsAvailable))
            {
                long total = ModelCatalog.SelectObject.Where(m => !_models.IsAvailable(m)).Sum(m => m.SizeBytes);
                Status = $"Downloading the Select Object model ({total / 1_000_000} MB, only the first time)…";
                var progress = new Progress<DownloadProgress>(p =>
                    Status = $"Downloading the Select Object model… {p.Fraction ?? 0:P0} of {total / 1_000_000} MB");
                await _models.GetAllAsync(ModelCatalog.SelectObject, progress);
            }
            Status = "Loading the Select Object model…";
            _sam = await Task.Run(() => SegmentAnything.Load(_models));
            Status = _sam.Device == InferenceDevice.DirectML
                ? "Select Object runs on the GPU (DirectML)."
                : $"Select Object runs on the CPU ({_sam.FallbackReason ?? "requested"}).";
            return _sam;
        }
        catch
        {
            _samLoading = null; // allow a retry
            throw;
        }
    }

    /// <summary>The encoder result for the open photo (computed once per photo, in the background).</summary>
    private Task<SamEmbedding> EmbeddingAsync(SegmentAnything sam, SKBitmap image)
    {
        if (_embedding is null || !ReferenceEquals(_embeddingFor, image) || _embedding.IsFaulted)
        {
            _embeddingFor = image;
            _embedding = Task.Run(() => sam.Encode(image));
        }
        return _embedding;
    }

    /// <summary>
    /// Handles a click (<paramref name="point"/>) or box from the viewer: refines the selected object
    /// component, or creates one (in the selected mask, or a new mask).
    /// </summary>
    public async Task SelectObjectAsync(SelectPoint? point, SelectBox? box)
    {
        if (Original is not { } original || EditBase is not { } image || _selecting)
            return;
        _selecting = true;
        try
        {
            // Refine the selected object component, if any.
            Guid? maskId = SelectedMask?.Id;
            int index = -1;
            RasterMaskComponent? existing = null;
            if (maskId is { } id && State.FindMask(id) is { } mask
                && SelectedComponentIndex >= 0 && SelectedComponentIndex < mask.Components.Count
                && mask.Components[SelectedComponentIndex] is RasterMaskComponent { Source: "object" } r)
            {
                existing = r;
                index = SelectedComponentIndex;
            }
            if (existing is null && point is { Include: false })
            {
                Status = "Click on the object first; Alt+click then removes parts from the selection.";
                return;
            }
            var points = existing?.Points ?? [];
            if (point is not null)
                points = points.Add(point);
            var selectBox = box ?? existing?.Box;

            var sam = await LoadSamAsync();
            bool firstForPhoto = !ReferenceEquals(_embeddingFor, image) || _embedding is null;
            if (firstForPhoto)
                Status = "Analysing the photo…";
            var embedding = await EmbeddingAsync(sam, image);
            if (!ReferenceEquals(Original, original))
                return; // another photo was opened meanwhile
            var selected = await Task.Run(() => sam.Select(embedding, points, selectBox, image.Width, image.Height));
            if (existing is not null)
                selected = selected with { Mode = existing.Mode, Invert = existing.Invert };

            if (existing is not null && maskId is { } refineId)
            {
                int i = index;
                EditMask(refineId, m => i < m.Components.Count ? m.ReplaceComponent(i, selected) : m, null);
            }
            else
            {
                Guid targetId;
                if (SelectedMask is { } current)
                {
                    targetId = current.Id;
                }
                else
                {
                    var created = new Mask { Name = State.NextMaskName() };
                    ApplyEdit(State.AddMask(created));
                    targetId = created.Id;
                }
                EditMask(targetId, m => m.AddComponent(selected), null);
                if (SelectedMask?.Id != targetId)
                    SelectedMask = Masks.FirstOrDefault(m => m.Id == targetId);
                SelectedComponentIndex = State.FindMask(targetId)!.Components.Count - 1;
            }
            OnPropertyChanged(nameof(EditableComponent));
            Status = "Object selected. Click to add parts, Alt+click to remove parts, drag a box to reframe; Esc when done.";
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException or System.IO.IOException
            or System.IO.InvalidDataException or Microsoft.ML.OnnxRuntime.OnnxRuntimeException or UnauthorizedAccessException)
        {
            Status = $"Select Object failed: {ex.Message}";
        }
        finally
        {
            _selecting = false;
        }
    }
}
