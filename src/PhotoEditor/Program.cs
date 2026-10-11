using Avalonia;
using System;

namespace PhotoEditor;

sealed class Program
{
    // Initialization code. Don't use any Avalonia, third-party APIs or any
    // SynchronizationContext-reliant code before AppMain is called: things aren't initialized
    // yet and stuff might break.
    [STAThread]
    public static void Main(string[] args) => BuildAvaloniaApp()
        .StartWithClassicDesktopLifetime(args);

    // Avalonia configuration, don't remove; also used by visual designer.
    public static AppBuilder BuildAvaloniaApp()
        => AppBuilder.Configure<App>()
            .UsePlatformDetect()
            // Avalonia's default GPU resource cache is a few tens of MB; the photo, its two RAW layers (headroom, fine)
            // and the tone maps did not fit, so Skia uploaded them again on every frame (Iris Xe: 12 ms per frame at
            // fit, 36 ms at 100 % with no adjustments at all; the full 24 MP photo with its layers is ~290 MB).
            .With(new SkiaOptions { MaxGpuResourceSizeBytes = 512L * 1024 * 1024 })
#if DEBUG
            .WithDeveloperTools()
#endif
            .WithInterFont()
            .LogToTrace();
}
