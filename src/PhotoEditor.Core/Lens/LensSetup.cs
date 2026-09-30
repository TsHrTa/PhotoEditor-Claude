using PhotoEditor.Core.Adjustments;
using PhotoEditor.Core.Ai;

namespace PhotoEditor.Core.Lens;

/// <summary>What the lens panel shows about the open photo's profile.</summary>
public sealed record LensMatch(PhotoLens Photo, LensfunCamera? Camera, LensfunLens? Lens, double CropFactor)
{
    public string Describe()
    {
        if (Lens is not null)
            return Lens.Name;
        if (string.IsNullOrWhiteSpace(Photo.LensName))
            return "The photo doesn't name its lens";
        return $"No profile for “{Photo.LensName}”";
    }
}

/// <summary>
/// Turns a photo's lens information and its settings into a <see cref="LensCorrection"/>; keeps the lensfun
/// database (downloaded on first use into <see cref="DefaultDirectory"/>).
/// </summary>
public static class LensSetup
{
    public static string DefaultDirectory =>
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "PhotoEditor", "lensfun");

    private static LensDatabase? _database;
    private static readonly object Gate = new();

    /// <summary>The database from <see cref="DefaultDirectory"/> (empty until downloaded).</summary>
    public static LensDatabase Database
    {
        get
        {
            lock (Gate)
                return _database ??= LensDatabase.LoadDirectory(DefaultDirectory);
        }
    }

    /// <summary>Uses <paramref name="database"/> from now on (tests; after a download: null reloads from disk).</summary>
    public static void SetDatabase(LensDatabase? database)
    {
        lock (Gate)
            _database = database;
    }

    public static bool IsDatabaseDownloaded =>
        Directory.Exists(DefaultDirectory) && Directory.EnumerateFiles(DefaultDirectory, "*.xml").Any();

    /// <summary>The camera and lens entries for the photo (null when not found), and its crop factor.</summary>
    public static LensMatch Match(PhotoLens photo, LensDatabase database)
    {
        var camera = database.FindCamera(photo.CameraMaker, photo.CameraModel);
        var lens = database.FindLens(photo.LensName, camera, photo.FocalLength);
        double crop = camera?.CropFactor
            ?? (photo.FocalLength35mm is > 0 && photo.FocalLength is > 0 ? photo.FocalLength35mm.Value / photo.FocalLength.Value : (double?)null)
            ?? lens?.CropFactor
            ?? 1;
        return new LensMatch(photo, camera, lens, crop);
    }

    /// <summary>
    /// The corrections <paramref name="settings"/> ask for on a <paramref name="width"/> × <paramref name="height"/>
    /// photo, or null when nothing moves or changes brightness (manual vignetting alone is done by the renderers
    /// from the settings). <paramref name="autoCa"/> = measured red / blue scales, used when the profile has no
    /// chromatic aberration data.
    /// </summary>
    public static LensCorrection? For(PhotoLens photo, AdjustmentSettings settings, int width, int height,
        LensDatabase? database = null, (double Red, double Blue)? autoCa = null)
    {
        LensProfile? profile = null;
        double crop = 1;
        if (settings.LensProfile || settings.RemoveChromaticAberration)
        {
            var match = Match(photo, database ?? Database);
            crop = match.CropFactor;
            if (match.Lens is not null && photo.FocalLength is > 0 and var focal)
            {
                var full = LensProfile.For(match.Lens, focal, photo.Aperture);
                profile = full with
                {
                    DistortionModel = settings.LensProfile ? full.DistortionModel : null,
                    Vignetting = settings.LensProfile ? full.Vignetting : null,
                    TcaModel = settings.RemoveChromaticAberration ? full.TcaModel : null,
                };
            }
        }
        double red = 1, blue = 1;
        if (settings.RemoveChromaticAberration && profile?.TcaModel is null && autoCa is { } ca)
            (red, blue) = ca;
        double manual = Math.Clamp(settings.LensDistortion, -100, 100) / 100;
        bool anything = profile is { DistortionModel: not null } or { TcaModel: not null } or { Vignetting: not null }
            || manual != 0 || red != 1 || blue != 1;
        return anything ? new LensCorrection(width, height, profile, crop, manual, red, blue) : null;
    }

    /// <summary>The database files (lensfun's data/db folder).</summary>
    public static readonly string[] DatabaseFiles =
    [
        "6x6", "actioncams", "compact-canon", "compact-casio", "compact-fujifilm", "compact-kodak", "compact-konica-minolta",
        "compact-leica", "compact-nikon", "compact-olympus", "compact-panasonic", "compact-pentax", "compact-ricoh",
        "compact-samsung", "compact-sigma", "compact-sony", "contax", "generic", "mil-canon", "mil-fujifilm", "mil-leica",
        "mil-nikon", "mil-olympus", "mil-panasonic", "mil-pentax", "mil-samsung", "mil-samyang", "mil-sigma", "mil-sony",
        "mil-tamron", "mil-tokina", "mil-zeiss", "misc", "rf-leica", "slr-canon", "slr-hasselblad", "slr-konica-minolta",
        "slr-leica", "slr-nikon", "slr-olympus", "slr-pentax", "slr-ricoh", "slr-samsung", "slr-samyang", "slr-schneider",
        "slr-sigma", "slr-soligor", "slr-sony", "slr-tamron", "slr-tokina", "slr-ussr", "slr-vivitar", "slr-zeiss",
    ];

    public const string DatabaseUrl = "https://raw.githubusercontent.com/lensfun/lensfun/master/data/db/";

    /// <summary>Downloads the database (≈ 5 MB) into <see cref="DefaultDirectory"/> and reloads it; files that fail are skipped.</summary>
    public static async Task<int> DownloadDatabaseAsync(HttpClient http, IProgress<DownloadProgress>? progress = null, CancellationToken cancel = default)
    {
        Directory.CreateDirectory(DefaultDirectory);
        int done = 0, ok = 0;
        foreach (var name in DatabaseFiles)
        {
            var path = Path.Combine(DefaultDirectory, name + ".xml");
            try
            {
                var xml = await http.GetStringAsync(DatabaseUrl + name + ".xml", cancel);
                System.Xml.Linq.XDocument.Parse(xml); // only keep what parses
                await File.WriteAllTextAsync(path + ".part", xml, cancel);
                File.Move(path + ".part", path, overwrite: true);
                ok++;
            }
            catch (Exception ex) when (ex is HttpRequestException or System.Xml.XmlException or IOException)
            {
                // skip this file
            }
            progress?.Report(new DownloadProgress(++done, DatabaseFiles.Length));
        }
        SetDatabase(null);
        return ok;
    }
}
