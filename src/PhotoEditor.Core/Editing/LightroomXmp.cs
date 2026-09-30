using System.Collections.Immutable;
using System.Globalization;
using System.Xml;
using System.Xml.Linq;
using PhotoEditor.Core.Adjustments;
using PhotoEditor.Core.Imaging;
using PhotoEditor.Core.Masks;
using SkiaSharp;

namespace PhotoEditor.Core.Editing;

/// <summary>
/// Upright image size (as edited), the file's EXIF orientation (stored → upright) and whether it is a RAW
/// file (Lightroom stores white balance differently for RAW and rendered images).
/// </summary>
public readonly record struct ImageGeometry(int Width, int Height, SKEncodedOrigin Orientation = SKEncodedOrigin.TopLeft, bool IsRaw = false)
{
    /// <summary>True when the stored (sensor) image is turned by 90° relative to the upright one.</summary>
    public bool IsQuarterTurn => Orientation is SKEncodedOrigin.LeftTop or SKEncodedOrigin.RightTop
        or SKEncodedOrigin.RightBottom or SKEncodedOrigin.LeftBottom;

    /// <summary>True when the orientation includes a mirror image.</summary>
    public bool IsMirrored => Orientation is SKEncodedOrigin.TopRight or SKEncodedOrigin.BottomLeft
        or SKEncodedOrigin.LeftTop or SKEncodedOrigin.RightBottom;

    public int StoredWidth => IsQuarterTurn ? Height : Width;
    public int StoredHeight => IsQuarterTurn ? Width : Height;

    /// <summary>Normalised upright → normalised stored coordinates.</summary>
    public (double X, double Y) ToStored(double x, double y)
    {
        ImageLoader.OrientationMatrix(Orientation, 1, 1).TryInvert(out var inverse);
        var p = inverse.MapPoint((float)x, (float)y);
        return (p.X, p.Y);
    }

    /// <summary>Normalised stored → normalised upright coordinates.</summary>
    public (double X, double Y) ToUpright(double x, double y)
    {
        var p = ImageLoader.OrientationMatrix(Orientation, 1, 1).MapPoint((float)x, (float)y);
        return (p.X, p.Y);
    }
}

/// <summary>
/// Reads and writes edits as Adobe Camera Raw settings (<c>crs:</c> namespace) in an XMP sidecar, the
/// format Lightroom Classic / Camera Raw use: <c>IMG_0001.CR3</c> → <c>IMG_0001.xmp</c>. Original image
/// files are never modified.
/// </summary>
/// <remarks>
/// Values are mapped slider-for-slider; the rendering math differs from Adobe's, so the look in Lightroom
/// is similar in intent, not identical. Crop and gradient coordinates are converted to the unrotated
/// (sensor) orientation Lightroom uses. Not representable (reported in <c>skipped</c>): brush masks, masks
/// with several components, HSL / vibrance / vignette inside masks, and white balance changes on RAW files
/// (Lightroom stores absolute Kelvin, which needs the camera's as-shot value; for JPEG etc. it stores relative
/// IncrementalTemperature / IncrementalTint, which are written).
/// When a sidecar already exists (e.g. written by Lightroom) only the settings this app owns are replaced;
/// ratings, keywords and all other Lightroom settings are kept.
/// </remarks>
public static class LightroomXmp
{
    private static readonly XNamespace X = "adobe:ns:meta/";
    private static readonly XNamespace Rdf = "http://www.w3.org/1999/02/22-rdf-syntax-ns#";
    private static readonly XNamespace Crs = "http://ns.adobe.com/camera-raw-settings/1.0/";
    private static readonly XNamespace Xmp = "http://ns.adobe.com/xap/1.0/";
    private static readonly XNamespace Tiff = "http://ns.adobe.com/tiff/1.0/";

    private const string LinearCorrections = "GradientBasedCorrections";
    private const string RadialCorrections = "CircularGradientBasedCorrections";

    /// <summary>Local exposure is stored as -1..1 for -4..+4 stops.</summary>
    private const double LocalExposureRange = 4;

    /// <summary>
    /// Proprietary RAW formats (CR3, NEF, …). Lightroom Classic reads sidecars automatically only for these;
    /// for JPEG / PNG / DNG it expects XMP inside the file. Sidecars are written for every format anyway
    /// (the originals are never changed).
    /// </summary>
    public static bool IsProprietaryRaw(string imagePath) =>
        RawImageLoader.IsRaw(imagePath) && !Path.GetExtension(imagePath).Equals(".dng", StringComparison.OrdinalIgnoreCase);

    /// <summary>
    /// Sidecar path: <c>IMG_0001.xmp</c> (Lightroom's convention). A non-proprietary-RAW file that shares its base
    /// name with another photo (RAW + JPEG pairs) uses <c>IMG_0001.JPG.xmp</c> instead, so the RAW keeps
    /// <c>IMG_0001.xmp</c> and neither overwrites the other.
    /// </summary>
    public static string PathFor(string imagePath)
    {
        var plain = Path.ChangeExtension(imagePath, ".xmp");
        if (IsProprietaryRaw(imagePath))
            return plain;
        var dir = Path.GetDirectoryName(Path.GetFullPath(imagePath));
        var baseName = Path.GetFileNameWithoutExtension(imagePath);
        bool shared = dir is not null && Directory.Exists(dir) && Directory
            .EnumerateFiles(dir, baseName + ".*")
            .Any(f => Path.GetFileNameWithoutExtension(f).Equals(baseName, StringComparison.OrdinalIgnoreCase)
                && ImageLoader.IsSupported(f)
                && !string.Equals(Path.GetFullPath(f), Path.GetFullPath(imagePath), StringComparison.OrdinalIgnoreCase));
        return shared ? imagePath + ".xmp" : plain;
    }

    // ---- Writing ----

    /// <summary>
    /// Returns the XMP document for <paramref name="state"/>, merged into <paramref name="existing"/> if given.
    /// <paramref name="skipped"/> lists what could not be written.
    /// </summary>
    public static string Write(EditState state, ImageGeometry geometry, string? existing, out IReadOnlyList<string> skipped)
    {
        var notes = new List<string>();
        var doc = existing is not null ? TryParse(existing) : null;
        doc ??= NewDocument();
        doc.Nodes().OfType<XProcessingInstruction>().Where(pi => pi.Target == "xpacket").Remove();
        var description = FindDescription(doc) ?? AddDescription(doc);
        bool fresh = existing is null || description.Attribute(Crs + "HasSettings") is null;

        void Set(string name, string value)
        {
            description.Elements(Crs + name).Remove();
            description.SetAttributeValue(Crs + name, value);
        }

        Set("Version", "15.0");
        Set("ProcessVersion", "11.0");
        Set("HasSettings", "True");
        var a = state.Adjustments;
        // This app's sliders go further than Lightroom's: the XMP gets Lightroom's limits.
        var limited = new SortedSet<string>();
        double Limit(double value, double max, string name)
        {
            if (Math.Abs(value) <= max)
                return value;
            limited.Add(name);
            return Math.Clamp(value, -max, max);
        }
        if (geometry.IsRaw)
        {
            if (fresh)
                Set("WhiteBalance", "As Shot");
            if (a.Temperature != 0 || a.Tint != 0)
                notes.Add("white balance (Lightroom needs absolute Kelvin for RAW files)");
        }
        else
        {
            Set("WhiteBalance", a.Temperature == 0 && a.Tint == 0 ? "As Shot" : "Custom");
            Set("IncrementalTemperature", Signed(Limit(a.Temperature, 100, "temperature")));
            Set("IncrementalTint", Signed(Limit(a.Tint, 100, "tint")));
        }
        if (a.DeblurAmount != 0)
            notes.Add("AI deblur (Lightroom has no equivalent setting)");
        if (state.Spots.Count > 0)
            notes.Add("spot removal (not written for Lightroom yet)");

        Set("Exposure2012", Signed(Limit(a.Exposure, 5, "exposure"), "0.00"));
        Set("Contrast2012", Signed(Limit(a.Contrast, 100, "contrast")));
        Set("Highlights2012", Signed(Limit(a.Highlights, 100, "highlights")));
        Set("Shadows2012", Signed(Limit(a.Shadows, 100, "shadows")));
        Set("Whites2012", Signed(Limit(a.Whites, 100, "whites")));
        Set("Blacks2012", Signed(Limit(a.Blacks, 100, "blacks")));
        Set("Vibrance", Signed(Limit(a.Vibrance, 100, "vibrance")));
        // Soften has no own Lightroom slider; its closest equivalent is negative Texture (smooths fine detail, keeps edges).
        Set("Texture", Signed(Limit(a.Texture - a.Soften, 100, "texture")));
        Set("Clarity2012", Signed(Limit(a.Clarity, 100, "clarity")));
        Set("Dehaze", Signed(a.Dehaze));
        // Lens corrections: Lightroom finds the profile for the lens itself ("LensDefaults").
        Set("LensProfileEnable", a.LensProfile ? "1" : "0");
        if (a.LensProfile)
            Set("LensProfileSetup", "LensDefaults");
        Set("AutoLateralCA", a.RemoveChromaticAberration ? "1" : "0");
        Set("LensManualDistortionAmount", Signed(a.LensDistortion));
        Set("VignetteAmount", Signed(a.LensVignetting));
        Set("Saturation", Signed(Limit(a.Saturation, 100, "saturation")));

        for (int i = 0; i < HslBands.Count; i++)
        {
            var band = HslBands.Get(a, i);
            Set("HueAdjustment" + LrBandNames[i], Signed(Limit(band.Hue, 100, "HSL")));
            Set("SaturationAdjustment" + LrBandNames[i], Signed(Limit(band.Saturation, 100, "HSL")));
            Set("LuminanceAdjustment" + LrBandNames[i], Signed(Limit(band.Luminance, 100, "HSL")));
        }

        Set("PostCropVignetteAmount", Signed(Limit(a.VignetteAmount, 100, "vignette")));
        Set("PostCropVignetteMidpoint", Number(a.VignetteMidpoint, "0"));
        Set("PostCropVignetteRoundness", Signed(a.VignetteRoundness));
        Set("PostCropVignetteFeather", Number(a.VignetteFeather, "0"));
        Set("PostCropVignetteStyle", "1");
        Set("PostCropVignetteHighlightContrast", "0");
        Set("Sharpness", Number(Math.Min(a.SharpenAmount, 150), "0"));
        if (a.SharpenAmount > 150)
            limited.Add("sharpening");
        Set("SharpenRadius", Signed(a.SharpenRadius, "0.0"));
        Set("SharpenEdgeMasking", Number(a.SharpenMasking, "0"));
        Set("LuminanceSmoothing", Number(a.NoiseLuminance, "0"));
        Set("ColorNoiseReduction", Number(a.NoiseColor, "0"));
        // Lightroom's defringe amounts run 0..20.
        Set("DefringePurpleAmount", Number(a.DefringePurple / 5, "0"));
        Set("DefringeGreenAmount", Number(a.DefringeGreen / 5, "0"));
        Set("DefringePurpleHueLo", Number(a.DefringePurpleHueLow, "0"));
        Set("DefringePurpleHueHi", Number(a.DefringePurpleHueHigh, "0"));
        Set("DefringeGreenHueLo", Number(a.DefringeGreenHueLow, "0"));
        Set("DefringeGreenHueHi", Number(a.DefringeGreenHueHigh, "0"));
        if (a.DenoiseAmount != 0)
            notes.Add("AI denoise (Lightroom has no equivalent setting; use its own Denoise)");

        if (limited.Count > 0)
            notes.Add($"{string.Join(", ", limited)} beyond Lightroom's range (written at Lightroom's limit)");

        // Lightroom keeps a rotation / flip as the picture's orientation: the file's own, then the user's.
        if (!state.Orientation.IsNone || description.Attribute(Tiff + "Orientation") is not null)
        {
            var total = state.Orientation.After(PhotoOrientation.FromOrigin(geometry.Orientation));
            if (description.GetNamespaceOfPrefix("tiff") is null)
                description.SetAttributeValue(XNamespace.Xmlns + "tiff", Tiff.NamespaceName);
            description.Elements(Tiff + "Orientation").Remove();
            description.SetAttributeValue(Tiff + "Orientation", ((int)total.ToOrigin()).ToString(CultureInfo.InvariantCulture));
        }

        WriteCrop(state.Crop, geometry, Set);
        WriteMasks(state.Masks, geometry, description, notes);

        skipped = notes;
        return Serialize(doc);
    }

    private static string Serialize(XDocument doc)
    {
        using var writer = new StringWriter(CultureInfo.InvariantCulture);
        doc.Save(writer, SaveOptions.None);
        // Drop the XML declaration; XMP sidecars start with <x:xmpmeta>.
        var text = writer.ToString();
        return text.StartsWith("<?xml", StringComparison.Ordinal) ? text[(text.IndexOf("?>", StringComparison.Ordinal) + 2)..].TrimStart() : text;
    }

    /// <summary>Lightroom's names for the eight HSL bands, in <see cref="HslBands"/> order.</summary>
    private static readonly string[] LrBandNames = ["Red", "Orange", "Yellow", "Green", "Aqua", "Blue", "Purple", "Magenta"];

    private static void WriteCrop(Crop crop, ImageGeometry g, Action<string, string> set)
    {
        var stored = ToStoredFrame(crop, g);
        set("CropTop", Number(stored.Top, "0.000000"));
        set("CropLeft", Number(stored.Left, "0.000000"));
        set("CropBottom", Number(stored.Bottom, "0.000000"));
        set("CropRight", Number(stored.Right, "0.000000"));
        // Lightroom's angle turns the photo (positive = clockwise); ours turns the frame over the photo.
        set("CropAngle", Number(-stored.Angle, "0.######"));
        set("CropConstrainToWarp", "0");
        set("HasCrop", crop.IsDefault ? "False" : "True");
    }

    /// <summary>The crop expressed in the stored (sensor) orientation, normalised to the stored size.</summary>
    private static Crop ToStoredFrame(Crop crop, ImageGeometry g)
    {
        var f = crop.Frame(g.Width, g.Height);
        var (cx, cy) = g.ToStored(f.CenterX / g.Width, f.CenterY / g.Height);
        var stored = new CropFrame(
            cx * g.StoredWidth, cy * g.StoredHeight,
            g.IsQuarterTurn ? f.HalfHeight : f.HalfWidth,
            g.IsQuarterTurn ? f.HalfWidth : f.HalfHeight,
            g.IsMirrored ? -f.Angle : f.Angle);
        return Crop.FromFrame(stored, g.StoredWidth, g.StoredHeight);
    }

    private static Crop FromStoredFrame(Crop stored, ImageGeometry g)
    {
        var f = stored.Frame(g.StoredWidth, g.StoredHeight);
        var (cx, cy) = g.ToUpright(f.CenterX / g.StoredWidth, f.CenterY / g.StoredHeight);
        var upright = new CropFrame(
            cx * g.Width, cy * g.Height,
            g.IsQuarterTurn ? f.HalfHeight : f.HalfWidth,
            g.IsQuarterTurn ? f.HalfWidth : f.HalfHeight,
            g.IsMirrored ? -f.Angle : f.Angle);
        return Crop.FromFrame(upright, g.Width, g.Height);
    }

    private static void WriteMasks(ImmutableList<Mask> masks, ImageGeometry g, XElement description, List<string> notes)
    {
        var linear = new List<XElement>();
        var radial = new List<XElement>();
        foreach (var mask in masks)
        {
            if (mask.Components.Count != 1 || mask.Components[0] is not (LinearGradientComponent or RadialGradientComponent))
            {
                if (mask.Components.Count > 0)
                    notes.Add($"mask \"{mask.Name}\" (only single linear / radial gradients can be written)");
                continue;
            }
            var correction = CorrectionDescription(mask, notes);
            var component = mask.Components[0];
            XElement maskItem;
            if (component is LinearGradientComponent l)
            {
                var (full, zero) = l.Invert ? (l.End, l.Start) : (l.Start, l.End);
                var fs = g.ToStored(full.X, full.Y);
                var zs = g.ToStored(zero.X, zero.Y);
                maskItem = MaskDescription("Mask/Gradient",
                    ("ZeroX", Number(zs.X, "0.000000")), ("ZeroY", Number(zs.Y, "0.000000")),
                    ("FullX", Number(fs.X, "0.000000")), ("FullY", Number(fs.Y, "0.000000")));
                linear.Add(CorrectionItem(correction, maskItem));
            }
            else
            {
                var r = (RadialGradientComponent)component;
                var (cx, cy) = g.ToStored(r.Center.X, r.Center.Y);
                double rx = r.RadiusX * g.Width, ry = r.RadiusY * g.Width; // pixels, upright axes
                if (g.IsQuarterTurn)
                    (rx, ry) = (ry, rx);
                double sx = cx * g.StoredWidth, sy = cy * g.StoredHeight;
                maskItem = MaskDescription("Mask/CircularGradient",
                    ("Top", Number((sy - ry) / g.StoredHeight, "0.000000")),
                    ("Left", Number((sx - rx) / g.StoredWidth, "0.000000")),
                    ("Bottom", Number((sy + ry) / g.StoredHeight, "0.000000")),
                    ("Right", Number((sx + rx) / g.StoredWidth, "0.000000")),
                    ("Angle", "0"),
                    ("Midpoint", "50"),
                    ("Roundness", "0"),
                    ("Feather", Number(r.Feather * 100, "0")),
                    ("Flipped", r.Invert ? "true" : "false"),
                    ("Version", "2"));
                radial.Add(CorrectionItem(correction, maskItem));
            }
            if (component.Mode != MaskMode.Add)
                notes.Add($"mode of mask \"{mask.Name}\"");
        }

        foreach (var (name, items) in new[] { (LinearCorrections, linear), (RadialCorrections, radial) })
        {
            description.Attributes(Crs + name).Remove();
            description.Elements(Crs + name).Remove();
            if (items.Count > 0)
                description.Add(new XElement(Crs + name, new XElement(Rdf + "Seq", items)));
        }
    }

    private static XElement CorrectionDescription(Mask mask, List<string> notes)
    {
        var a = mask.Adjustments;
        var dropped = new List<string>();
        if (a.Vibrance != 0) dropped.Add("vibrance");
        if (Enumerable.Range(0, HslBands.Count).Any(i => HslBands.Get(a, i) != HslBand.Zero)) dropped.Add("HSL");
        if (a.VignetteAmount != 0) dropped.Add("vignette");
        if (a.SharpenAmount != 0) dropped.Add("sharpening");
        if (dropped.Count > 0)
            notes.Add($"{string.Join(", ", dropped)} of mask \"{mask.Name}\"");

        return new XElement(Rdf + "Description",
            new XAttribute(Crs + "What", "Correction"),
            new XAttribute(Crs + "CorrectionName", mask.Name),
            new XAttribute(Crs + "CorrectionAmount", "1"),
            new XAttribute(Crs + "CorrectionActive", mask.Enabled ? "true" : "false"),
            // Lightroom's local values run from −1 to +1.
            new XAttribute(Crs + "LocalExposure2012", Local(a.Exposure / LocalExposureRange)),
            new XAttribute(Crs + "LocalContrast2012", Local(a.Contrast / 100)),
            new XAttribute(Crs + "LocalHighlights2012", Local(a.Highlights / 100)),
            new XAttribute(Crs + "LocalShadows2012", Local(a.Shadows / 100)),
            new XAttribute(Crs + "LocalWhites2012", Local(a.Whites / 100)),
            new XAttribute(Crs + "LocalBlacks2012", Local(a.Blacks / 100)),
            new XAttribute(Crs + "LocalTemperature", Local(a.Temperature / 100)),
            new XAttribute(Crs + "LocalTint", Local(a.Tint / 100)),
            new XAttribute(Crs + "LocalSaturation", Local(a.Saturation / 100)),
            new XAttribute(Crs + "LocalTexture", Local((a.Texture - a.Soften) / 100)),
            new XAttribute(Crs + "LocalClarity2012", Local(a.Clarity / 100)),
            new XAttribute(Crs + "LocalDehaze", Local(a.Dehaze / 100)));

        static string Local(double value) => Number(Math.Clamp(value, -1, 1), "0.000000");
    }

    private static XElement MaskDescription(string what, params (string Name, string Value)[] values) =>
        new(Rdf + "Description",
            new XAttribute(Crs + "What", what),
            new XAttribute(Crs + "MaskValue", "1"),
            values.Select(v => new XAttribute(Crs + v.Name, v.Value)));

    private static XElement CorrectionItem(XElement correction, XElement mask)
    {
        correction.Add(new XElement(Crs + "CorrectionMasks", new XElement(Rdf + "Seq", new XElement(Rdf + "li", mask))));
        return new XElement(Rdf + "li", correction);
    }

    private static XDocument NewDocument() => new(
        new XElement(X + "xmpmeta",
            new XAttribute(XNamespace.Xmlns + "x", X.NamespaceName),
            new XAttribute(X + "xmptk", "PhotoEditor"),
            new XElement(Rdf + "RDF",
                new XAttribute(XNamespace.Xmlns + "rdf", Rdf.NamespaceName))));

    private static XElement AddDescription(XDocument doc)
    {
        var rdf = doc.Descendants(Rdf + "RDF").FirstOrDefault();
        if (rdf is null)
        {
            doc.Root!.Add(rdf = new XElement(Rdf + "RDF", new XAttribute(XNamespace.Xmlns + "rdf", Rdf.NamespaceName)));
        }
        var description = new XElement(Rdf + "Description",
            new XAttribute(Rdf + "about", ""),
            new XAttribute(XNamespace.Xmlns + "crs", Crs.NamespaceName));
        rdf.Add(description);
        return description;
    }

    /// <summary>The top-level rdf:Description holding crs settings (or the first one).</summary>
    private static XElement? FindDescription(XDocument doc)
    {
        var rdf = doc.Descendants(Rdf + "RDF").FirstOrDefault();
        var top = rdf?.Elements(Rdf + "Description").ToList() ?? [];
        var description = top.FirstOrDefault(d => d.Attributes().Any(a => a.Name.Namespace == Crs) || d.Elements().Any(e => e.Name.Namespace == Crs))
            ?? top.FirstOrDefault();
        if (description is not null && description.GetNamespaceOfPrefix("crs") is null
            && !description.Attributes().Any(a => a.IsNamespaceDeclaration && a.Value == Crs.NamespaceName))
            description.SetAttributeValue(XNamespace.Xmlns + "crs", Crs.NamespaceName);
        return description;
    }

    private static XDocument? TryParse(string xml)
    {
        try
        {
            return XDocument.Parse(xml, LoadOptions.PreserveWhitespace);
        }
        catch (XmlException)
        {
            return null;
        }
    }

    private static string Signed(double value, string format = "0") =>
        value.ToString($"+{format};-{format};{format}", CultureInfo.InvariantCulture);

    private static string Number(double value, string format) => value.ToString(format, CultureInfo.InvariantCulture);

    // ---- Reading ----

    /// <summary>Reads the settings this app understands from Lightroom / Camera Raw XMP; the rest is ignored.</summary>
    public static EditState Read(string xml, ImageGeometry geometry)
    {
        var doc = TryParse(xml) ?? throw new FormatException("The XMP file is not valid XML.");
        var d = FindDescription(doc);
        if (d is null)
            return EditState.Default;

        double Get(string name, double fallback = 0) =>
            ReadNumber(d, name) is { } v ? v : fallback;

        var a = AdjustmentSettings.Default with
        {
            Exposure = Get("Exposure2012"),
            Contrast = Get("Contrast2012"),
            Highlights = Get("Highlights2012"),
            Shadows = Get("Shadows2012"),
            Whites = Get("Whites2012"),
            Blacks = Get("Blacks2012"),
            Vibrance = Get("Vibrance"),
            Texture = Get("Texture"),
            Clarity = Get("Clarity2012"),
            Dehaze = Get("Dehaze"),
            LensProfile = Get("LensProfileEnable") != 0,
            RemoveChromaticAberration = Get("AutoLateralCA") != 0,
            LensDistortion = Get("LensManualDistortionAmount"),
            LensVignetting = Get("VignetteAmount"),
            Temperature = geometry.IsRaw ? 0 : Get("IncrementalTemperature"),
            Tint = geometry.IsRaw ? 0 : Get("IncrementalTint"),
            Saturation = Get("Saturation"),
            VignetteAmount = Get("PostCropVignetteAmount"),
            VignetteMidpoint = Get("PostCropVignetteMidpoint", 50),
            VignetteRoundness = Get("PostCropVignetteRoundness"),
            VignetteFeather = Get("PostCropVignetteFeather", 50),
            // Missing values are Camera Raw's defaults, which for RAWs include sharpening and colour noise reduction.
            SharpenAmount = Get("Sharpness", AdjustmentSettings.DefaultFor(geometry.IsRaw).SharpenAmount),
            SharpenRadius = Get("SharpenRadius", 1),
            SharpenMasking = Get("SharpenEdgeMasking"),
            NoiseLuminance = Get("LuminanceSmoothing"),
            NoiseColor = Get("ColorNoiseReduction", AdjustmentSettings.DefaultFor(geometry.IsRaw).NoiseColor),
            DefringePurple = Get("DefringePurpleAmount") * 5,
            DefringeGreen = Get("DefringeGreenAmount") * 5,
            DefringePurpleHueLow = Get("DefringePurpleHueLo", 30),
            DefringePurpleHueHigh = Get("DefringePurpleHueHi", 70),
            DefringeGreenHueLow = Get("DefringeGreenHueLo", 40),
            DefringeGreenHueHigh = Get("DefringeGreenHueHi", 60),
        };
        for (int i = 0; i < HslBands.Count; i++)
        {
            a = HslBands.With(a, i, new HslBand(
                Get("HueAdjustment" + LrBandNames[i]),
                Get("SaturationAdjustment" + LrBandNames[i]),
                Get("LuminanceAdjustment" + LrBandNames[i])));
        }
        a = Clamp(a);

        var crop = Crop.None;
        if (string.Equals(ReadText(d, "HasCrop"), "True", StringComparison.OrdinalIgnoreCase))
        {
            var stored = new Crop
            {
                Left = Get("CropLeft"),
                Top = Get("CropTop"),
                Right = Get("CropRight", 1),
                Bottom = Get("CropBottom", 1),
                Angle = -Get("CropAngle"),
            };
            crop = CropGeometry.Normalize(FromStoredFrame(CropGeometry.Normalize(stored), geometry));
        }

        var masks = ImmutableList.CreateBuilder<Mask>();
        foreach (var (name, isRadial) in new[] { (LinearCorrections, false), (RadialCorrections, true) })
        {
            var items = d.Element(Crs + name)?.Element(Rdf + "Seq")?.Elements(Rdf + "li") ?? [];
            foreach (var item in items)
            {
                var correction = item.Element(Rdf + "Description") ?? item;
                var maskItem = correction.Element(Crs + "CorrectionMasks")?.Element(Rdf + "Seq")?.Elements(Rdf + "li").FirstOrDefault();
                var maskDescription = maskItem?.Element(Rdf + "Description") ?? maskItem;
                if (maskDescription is null)
                    continue;
                MaskComponent component = isRadial ? ReadRadial(maskDescription, geometry) : ReadLinear(maskDescription, geometry);
                masks.Add(new Mask
                {
                    Name = ReadText(correction, "CorrectionName") is { Length: > 0 } n ? n : $"Mask {masks.Count + 1}",
                    Enabled = !string.Equals(ReadText(correction, "CorrectionActive"), "false", StringComparison.OrdinalIgnoreCase),
                    Adjustments = Clamp(AdjustmentSettings.Default with
                    {
                        Exposure = (ReadNumber(correction, "LocalExposure2012") ?? 0) * LocalExposureRange,
                        Contrast = (ReadNumber(correction, "LocalContrast2012") ?? 0) * 100,
                        Highlights = (ReadNumber(correction, "LocalHighlights2012") ?? 0) * 100,
                        Shadows = (ReadNumber(correction, "LocalShadows2012") ?? 0) * 100,
                        Whites = (ReadNumber(correction, "LocalWhites2012") ?? 0) * 100,
                        Blacks = (ReadNumber(correction, "LocalBlacks2012") ?? 0) * 100,
                        Temperature = (ReadNumber(correction, "LocalTemperature") ?? 0) * 100,
                        Tint = (ReadNumber(correction, "LocalTint") ?? 0) * 100,
                        Saturation = (ReadNumber(correction, "LocalSaturation") ?? 0) * 100,
                        Texture = (ReadNumber(correction, "LocalTexture") ?? 0) * 100,
                        Clarity = (ReadNumber(correction, "LocalClarity2012") ?? 0) * 100,
                        Dehaze = (ReadNumber(correction, "LocalDehaze") ?? 0) * 100,
                    }),
                    Components = [component],
                });
            }
        }

        // tiff:Orientation = the file's orientation, then the user's rotation / flip.
        var orientation = PhotoOrientation.None;
        if ((d.Attribute(Tiff + "Orientation")?.Value ?? d.Element(Tiff + "Orientation")?.Value) is { } text
            && int.TryParse(text, NumberStyles.Integer, CultureInfo.InvariantCulture, out int code) && code is >= 1 and <= 8)
        {
            var file = PhotoOrientation.FromOrigin(geometry.Orientation);
            orientation = PhotoOrientation.FromOrigin((SKEncodedOrigin)code).After(file.Inverse());
        }
        return new EditState { Adjustments = a, Crop = crop, Masks = masks.ToImmutable(), Orientation = orientation };
    }

    private static LinearGradientComponent ReadLinear(XElement m, ImageGeometry g)
    {
        var full = g.ToUpright(ReadNumber(m, "FullX") ?? 0.5, ReadNumber(m, "FullY") ?? 0.25);
        var zero = g.ToUpright(ReadNumber(m, "ZeroX") ?? 0.5, ReadNumber(m, "ZeroY") ?? 0.5);
        return new LinearGradientComponent
        {
            Start = new BrushPoint((float)full.X, (float)full.Y),
            End = new BrushPoint((float)zero.X, (float)zero.Y),
        };
    }

    private static RadialGradientComponent ReadRadial(XElement m, ImageGeometry g)
    {
        double left = ReadNumber(m, "Left") ?? 0.3, right = ReadNumber(m, "Right") ?? 0.7;
        double top = ReadNumber(m, "Top") ?? 0.3, bottom = ReadNumber(m, "Bottom") ?? 0.7;
        var (cx, cy) = g.ToUpright((left + right) / 2, (top + bottom) / 2);
        double rx = (right - left) / 2 * g.StoredWidth, ry = (bottom - top) / 2 * g.StoredHeight;
        if (g.IsQuarterTurn)
            (rx, ry) = (ry, rx);
        return new RadialGradientComponent
        {
            Center = new BrushPoint((float)cx, (float)cy),
            RadiusX = (float)Math.Abs(rx / g.Width),
            RadiusY = (float)Math.Abs(ry / g.Width),
            Feather = (float)Math.Clamp((ReadNumber(m, "Feather") ?? 50) / 100, 0, 1),
            Invert = string.Equals(ReadText(m, "Flipped"), "true", StringComparison.OrdinalIgnoreCase),
        };
    }

    /// <summary>A crs value written as attribute or as child element.</summary>
    private static string? ReadText(XElement e, string name) =>
        e.Attribute(Crs + name)?.Value ?? e.Element(Crs + name)?.Value;

    private static double? ReadNumber(XElement e, string name) =>
        double.TryParse(ReadText(e, name), NumberStyles.Float, CultureInfo.InvariantCulture, out var v) && double.IsFinite(v) ? v : null;

    private static AdjustmentSettings Clamp(AdjustmentSettings s)
    {
        foreach (var p in AdjustmentParameters.All)
            s = p.Set(s, p.Get(s));
        return s;
    }

    // ---- Files ----

    /// <summary>Writes (or updates) the sidecar of <paramref name="imagePath"/> atomically; returns what was skipped.</summary>
    public static IReadOnlyList<string> Save(string imagePath, EditState state, ImageGeometry geometry)
    {
        var path = PathFor(imagePath);
        var existing = File.Exists(path) ? File.ReadAllText(path) : null;
        var xml = Write(state, geometry, existing, out var skipped);
        var temp = path + ".tmp";
        File.WriteAllText(temp, xml);
        File.Move(temp, path, overwrite: true);
        return skipped;
    }

    /// <summary>True when the XMP sidecar holds Camera Raw settings (an edit), not just e.g. a rating.</summary>
    public static bool HasSettings(string imagePath)
    {
        var path = PathFor(imagePath);
        if (!File.Exists(path) || TryParse(File.ReadAllText(path)) is not { } doc)
            return false;
        return doc.Descendants(Rdf + "Description").Any(d =>
            d.Attributes().Any(a => a.Name.Namespace == Crs) || d.Elements().Any(e => e.Name.Namespace == Crs));
    }

    /// <summary>The xmp:Rating of the sidecar (−1 = rejected, 0–5 stars); null if there is none.</summary>
    public static int? ReadRating(string imagePath)
    {
        var path = PathFor(imagePath);
        if (!File.Exists(path) || TryParse(File.ReadAllText(path)) is not { } doc)
            return null;
        var text = doc.Descendants(Rdf + "Description")
            .Select(d => d.Attribute(Xmp + "Rating")?.Value ?? d.Element(Xmp + "Rating")?.Value)
            .FirstOrDefault(v => v is not null);
        return double.TryParse(text, NumberStyles.Float, CultureInfo.InvariantCulture, out var value)
            ? (int)Math.Round(value)
            : null;
    }

    /// <summary>Sets xmp:Rating in the sidecar (created if needed, other content kept); 0 removes it.</summary>
    public static void SaveRating(string imagePath, int rating)
    {
        var path = PathFor(imagePath);
        var existing = File.Exists(path) ? File.ReadAllText(path) : null;
        if (existing is null && rating == 0)
            return;
        var doc = (existing is not null ? TryParse(existing) : null) ?? NewDocument();
        doc.Nodes().OfType<XProcessingInstruction>().Where(pi => pi.Target == "xpacket").Remove();
        var description = FindDescription(doc) ?? AddDescription(doc);
        foreach (var d in doc.Descendants(Rdf + "Description"))
            d.Elements(Xmp + "Rating").Remove();
        if (description.GetNamespaceOfPrefix("xmp") is null)
            description.SetAttributeValue(XNamespace.Xmlns + "xmp", Xmp.NamespaceName);
        description.SetAttributeValue(Xmp + "Rating", rating == 0 ? null : rating.ToString(CultureInfo.InvariantCulture));
        var temp = path + ".tmp";
        File.WriteAllText(temp, Serialize(doc));
        File.Move(temp, path, overwrite: true);
    }

    /// <summary>Reads the sidecar of <paramref name="imagePath"/>; null if there is none or it has no Camera Raw settings.</summary>
    public static EditState? Load(string imagePath, ImageGeometry geometry)
    {
        var path = PathFor(imagePath);
        if (!File.Exists(path))
            return null;
        var xml = File.ReadAllText(path);
        return xml.Contains(Crs.NamespaceName, StringComparison.Ordinal) ? Read(xml, geometry) : null;
    }
}
