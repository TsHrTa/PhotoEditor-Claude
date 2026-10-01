using System;
using System.Threading.Tasks;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using PhotoEditor.Core.Adjustments;
using PhotoEditor.Core.Lens;
using SkiaSharp;

namespace PhotoEditor.ViewModels;

/// <summary>Upright: sets the Transform from the photo's straight lines.</summary>
public partial class MainViewModel
{
    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(UprightCommand))]
    public partial bool IsUprightRunning { get; private set; }

    private bool CanUpright() => Original is not null && !IsUprightRunning;

    /// <summary>Finds the photo's lines and sets vertical / horizontal / rotate for <paramref name="mode"/> ("Off" clears them).</summary>
    [RelayCommand(CanExecute = nameof(CanUpright))]
    private async Task UprightAsync(string mode)
    {
        if (mode == "Off")
        {
            ApplyEdit(State with { Adjustments = State.Adjustments with { TransformVertical = 0, TransformHorizontal = 0, TransformRotate = 0 } });
            return;
        }
        if (!Enum.TryParse<UprightMode>(mode, out var uprightMode) || (_workingPreview ?? _originalPreview) is not { } basePreview)
            return;
        // The lines are measured on the photo with its lens corrections (they straighten curved lines) but without a Transform.
        var settings = State.Adjustments with
        {
            TransformVertical = 0, TransformHorizontal = 0, TransformRotate = 0, TransformAspect = 0, TransformScale = 100,
            TransformOffsetX = 0, TransformOffsetY = 0,
        };
        var lens = PhotoLensInfo;
        var path = FilePath;
        IsUprightRunning = true;
        Status = $"Upright ({uprightMode}): looking for straight lines…";
        try
        {
            var result = await Task.Run(() =>
            {
                using var photo = SKBitmap.FromImage(basePreview.Preview);
                var correction = LensSetup.For(lens, settings, photo.Width, photo.Height, measureCa: () => (1, 1));
                var corrected = correction?.Apply(photo) ?? photo;
                try
                {
                    return Upright.Estimate(corrected, uprightMode);
                }
                finally
                {
                    if (!ReferenceEquals(corrected, photo))
                        corrected.Dispose();
                }
            });
            if (FilePath != path)
                return;
            if (result is not { } r)
            {
                Status = "Upright: not enough straight lines in this photo.";
                return;
            }
            ApplyEdit(State with
            {
                Adjustments = State.Adjustments with { TransformVertical = r.Vertical, TransformHorizontal = r.Horizontal, TransformRotate = r.Rotate },
            });
            Status = $"Upright ({uprightMode}): vertical {r.Vertical:0}, horizontal {r.Horizontal:0}, rotate {r.Rotate:0.0}°";
        }
        catch (Exception ex) when (ex is InvalidOperationException or ArgumentException)
        {
            Status = $"Upright failed: {ex.Message}";
        }
        finally
        {
            IsUprightRunning = false;
        }
    }
}
