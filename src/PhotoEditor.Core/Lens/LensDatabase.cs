using System.Globalization;
using System.Xml.Linq;

namespace PhotoEditor.Core.Lens;

/// <summary>Distortion models of the lensfun database (all in Hugin coordinates: r = 1 at half the short side).</summary>
public enum DistortionModel
{
    /// <summary>r_d = r_u (1 − k1 + k1 r_u²).</summary>
    Poly3,

    /// <summary>r_d = r_u (1 + k1 r_u² + k2 r_u⁴).</summary>
    Poly5,

    /// <summary>r_d = r_u (a r_u³ + b r_u² + c r_u + 1 − a − b − c).</summary>
    PtLens,
}

public enum TcaModel
{
    /// <summary>r_d = r_u · k (per colour).</summary>
    Linear,

    /// <summary>r_d = r_u (b r_u² + c r_u + v) (per colour).</summary>
    Poly3,
}

/// <summary>Distortion measured at one focal length: model and its terms (k1 [k2] or a b c).</summary>
public sealed record DistortionCalibration(double Focal, DistortionModel Model, double[] Terms);

/// <summary>Lateral chromatic aberration at one focal length: red and blue terms (k, or v c b).</summary>
public sealed record TcaCalibration(double Focal, TcaModel Model, double[] Red, double[] Blue);

/// <summary>Vignetting (lensfun "pa" model, r = 1 at the corner) at one focal length, aperture and distance.</summary>
public sealed record VignettingCalibration(double Focal, double Aperture, double Distance, double K1, double K2, double K3);

/// <summary>A camera body: its mount and the crop factor of its sensor.</summary>
public sealed record LensfunCamera(string Maker, IReadOnlyList<string> Models, string Mount, double CropFactor);

/// <summary>A lens with its calibration data.</summary>
public sealed record LensfunLens(
    string Maker,
    IReadOnlyList<string> Models,
    IReadOnlyList<string> Mounts,
    double CropFactor,
    double AspectRatio,
    bool Rectilinear,
    IReadOnlyList<DistortionCalibration> Distortion,
    IReadOnlyList<TcaCalibration> Tca,
    IReadOnlyList<VignettingCalibration> Vignetting)
{
    /// <summary>The model name to show (the first, usually "Canon EF 24-105mm f/4L IS USM").</summary>
    public string Name => Models.Count > 0 ? Models[0] : Maker;
}

/// <summary>
/// The lensfun database (https://github.com/lensfun/lensfun, data under CC BY-SA 3.0): cameras, mounts and lens
/// calibrations read from its XML files, and matching of a photo's camera / lens names to its entries.
/// </summary>
public sealed class LensDatabase
{
    private readonly List<LensfunCamera> _cameras = [];
    private readonly List<LensfunLens> _lenses = [];
    private readonly Dictionary<string, HashSet<string>> _mountCompat = new(StringComparer.OrdinalIgnoreCase);

    public IReadOnlyList<LensfunCamera> Cameras => _cameras;
    public IReadOnlyList<LensfunLens> Lenses => _lenses;

    /// <summary>Reads every *.xml file of <paramref name="directory"/>; files that can't be read are skipped.</summary>
    public static LensDatabase LoadDirectory(string directory)
    {
        var db = new LensDatabase();
        if (!Directory.Exists(directory))
            return db;
        foreach (var file in Directory.EnumerateFiles(directory, "*.xml").Order(StringComparer.Ordinal))
        {
            try
            {
                db.Add(XDocument.Load(file));
            }
            catch (Exception ex) when (ex is System.Xml.XmlException or IOException or FormatException)
            {
                // a broken or partial file: the others still work
            }
        }
        return db;
    }

    /// <summary>A database from XML text (tests).</summary>
    public static LensDatabase Parse(params string[] xml)
    {
        var db = new LensDatabase();
        foreach (var text in xml)
            db.Add(XDocument.Parse(text));
        return db;
    }

    private void Add(XDocument doc)
    {
        var root = doc.Root;
        if (root is null)
            return;
        foreach (var mount in root.Elements("mount"))
        {
            var name = Text(mount.Element("name"));
            if (name is null)
                continue;
            if (!_mountCompat.TryGetValue(name, out var set))
                _mountCompat[name] = set = new(StringComparer.OrdinalIgnoreCase);
            foreach (var compat in mount.Elements("compat"))
                if (Text(compat) is { } c)
                    set.Add(c);
        }
        foreach (var camera in root.Elements("camera"))
        {
            var models = camera.Elements("model").Select(Text).OfType<string>().ToList();
            if (models.Count == 0 || Text(camera.Element("mount")) is not { } mount)
                continue;
            _cameras.Add(new LensfunCamera(Text(camera.Element("maker")) ?? "", models, mount,
                Number(Text(camera.Element("cropfactor"))) ?? 1));
        }
        foreach (var lens in root.Elements("lens"))
        {
            var models = lens.Elements("model").Select(Text).OfType<string>().ToList();
            if (models.Count == 0)
                continue;
            var type = Text(lens.Element("type"));
            var calibration = lens.Element("calibration");
            var distortion = new List<DistortionCalibration>();
            var tca = new List<TcaCalibration>();
            var vignetting = new List<VignettingCalibration>();
            foreach (var e in calibration?.Elements() ?? [])
            {
                double? focal = Number((string?)e.Attribute("focal"));
                if (focal is null)
                    continue;
                string? model = (string?)e.Attribute("model");
                double A(string name, double fallback) => Number((string?)e.Attribute(name)) ?? fallback;
                switch (e.Name.LocalName, model)
                {
                    case ("distortion", "poly3"):
                        distortion.Add(new(focal.Value, DistortionModel.Poly3, [A("k1", 0)]));
                        break;
                    case ("distortion", "poly5"):
                        distortion.Add(new(focal.Value, DistortionModel.Poly5, [A("k1", 0), A("k2", 0)]));
                        break;
                    case ("distortion", "ptlens"):
                        distortion.Add(new(focal.Value, DistortionModel.PtLens, [A("a", 0), A("b", 0), A("c", 0)]));
                        break;
                    case ("tca", "linear"):
                        tca.Add(new(focal.Value, TcaModel.Linear, [A("kr", 1)], [A("kb", 1)]));
                        break;
                    case ("tca", "poly3"):
                        tca.Add(new(focal.Value, TcaModel.Poly3, [A("vr", 1), A("cr", 0), A("br", 0)], [A("vb", 1), A("cb", 0), A("bb", 0)]));
                        break;
                    case ("vignetting", "pa"):
                        vignetting.Add(new(focal.Value, A("aperture", 0), A("distance", 1000), A("k1", 0), A("k2", 0), A("k3", 0)));
                        break;
                }
            }
            _lenses.Add(new LensfunLens(
                Text(lens.Element("maker")) ?? "",
                models,
                lens.Elements("mount").Select(Text).OfType<string>().ToList(),
                Number(Text(lens.Element("cropfactor"))) ?? 1,
                AspectRatio(Text(lens.Element("aspect-ratio"))),
                type is null or "rectilinear",
                distortion, tca, vignetting));
        }
    }

    private static string? Text(XElement? e) => e?.Value.Trim() is { Length: > 0 } v ? v : null;

    private static double? Number(string? text) =>
        double.TryParse(text, NumberStyles.Float, CultureInfo.InvariantCulture, out var v) && double.IsFinite(v) ? v : null;

    /// <summary>"3:2" or "1.5" (lensfun's default is 3:2).</summary>
    private static double AspectRatio(string? text)
    {
        if (text is null)
            return 1.5;
        var parts = text.Split(':');
        double ratio = parts.Length == 2 && Number(parts[0]) is { } a && Number(parts[1]) is { } b && b > 0 ? a / b : Number(text) ?? 1.5;
        return ratio >= 1 ? ratio : 1 / ratio;
    }

    // ---- Matching ----

    /// <summary>
    /// Lower-case letter and number runs of a name, without the maker: "EF24-105mm f/4L IS USM" and
    /// "Canon EF 24-105mm f/4L IS USM" both give ef 24 105 mm f 4 l is usm.
    /// </summary>
    public static IReadOnlyList<string> Tokens(string name, string? maker = null)
    {
        var tokens = new List<string>();
        var current = new System.Text.StringBuilder();
        bool? digits = null;
        void Flush()
        {
            if (current.Length > 0)
                tokens.Add(current.ToString());
            current.Clear();
        }
        foreach (char ch in name.ToLowerInvariant())
        {
            bool isDigit = char.IsDigit(ch) || (ch == '.' && digits == true);
            if (!char.IsLetterOrDigit(ch) && !(ch == '.' && digits == true))
            {
                Flush();
                digits = null;
                continue;
            }
            if (digits is { } d && d != isDigit)
                Flush();
            current.Append(ch);
            digits = isDigit;
        }
        Flush();
        // "4.0" is written "4" elsewhere
        for (int i = 0; i < tokens.Count; i++)
            if (tokens[i].Contains('.') && double.TryParse(tokens[i], NumberStyles.Float, CultureInfo.InvariantCulture, out var v))
                tokens[i] = v.ToString(CultureInfo.InvariantCulture);
        if (maker is not null)
        {
            var makerTokens = Tokens(maker);
            tokens.RemoveAll(makerTokens.Contains);
        }
        return tokens;
    }

    /// <summary>The camera with this maker / model, or null.</summary>
    public LensfunCamera? FindCamera(string? maker, string? model)
    {
        if (string.IsNullOrWhiteSpace(model))
            return null;
        var wanted = Tokens(model, maker).ToHashSet();
        return _cameras
            .Where(c => maker is null || Tokens(maker).Intersect(Tokens(c.Maker)).Any())
            .FirstOrDefault(c => c.Models.Any(m => Tokens(m, c.Maker).ToHashSet().SetEquals(wanted)));
    }

    /// <summary>
    /// The lens matching <paramref name="lensName"/> (as the camera reports it): same numbers (focal range,
    /// aperture), most words in common, a mount that fits <paramref name="camera"/> when known; null if nothing
    /// is close enough. <paramref name="focal"/> must be within the lens's range.
    /// </summary>
    public LensfunLens? FindLens(string? lensName, LensfunCamera? camera = null, double? focal = null)
    {
        if (string.IsNullOrWhiteSpace(lensName))
            return null;
        var wanted = Tokens(lensName).ToHashSet();
        var wantedNumbers = wanted.Where(IsNumber).ToHashSet();
        if (wantedNumbers.Count == 0 || wantedNumbers.All(n => n == "0"))
            return null;
        var mounts = camera is null ? null : MountsFitting(camera.Mount);
        LensfunLens? best = null;
        double bestScore = 0.6; // at least 60 % of the words in common
        foreach (var lens in _lenses)
        {
            if (!lens.Rectilinear)
                continue;
            foreach (var model in lens.Models)
            {
                var tokens = Tokens(model, lens.Maker).ToHashSet();
                if (!tokens.Where(IsNumber).ToHashSet().SetEquals(wantedNumbers))
                    continue;
                double score = (double)tokens.Intersect(wanted).Count() / tokens.Union(wanted).Count();
                if (mounts is not null && lens.Mounts.Any(mounts.Contains))
                    score += 0.05;
                if (focal is { } f && !InRange(lens, f))
                    score -= 0.5;
                if (score > bestScore)
                {
                    bestScore = score;
                    best = lens;
                }
            }
        }
        return best;
    }

    private static bool IsNumber(string token) => token.Length > 0 && char.IsDigit(token[0]);

    private static bool InRange(LensfunLens lens, double focal)
    {
        var focals = lens.Distortion.Select(d => d.Focal).Concat(lens.Vignetting.Select(v => v.Focal)).ToList();
        return focals.Count == 0 || (focal >= focals.Min() * 0.9 && focal <= focals.Max() * 1.1);
    }

    /// <summary>The camera's mount and the mounts it takes through adapters listed in the database.</summary>
    private HashSet<string> MountsFitting(string mount)
    {
        var set = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { mount };
        if (_mountCompat.TryGetValue(mount, out var compat))
            set.UnionWith(compat);
        return set;
    }
}
