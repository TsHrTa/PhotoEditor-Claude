namespace PhotoEditor.Core.Imaging;

/// <summary>
/// Rebuilds the channels the sensor clipped (white-balanced camera RGB, channel c clipped at clip[c]). Clipped
/// highlights are nearly always neutral or pale, so a clipped channel is raised towards the average of the other two
/// channels — their texture carries over, instead of a flat plateau at the clip level (which also tints: green clips
/// first, so clipped white turned pink). The clipped channels are solved together, a clipped one counting with its
/// clip level (a lower bound): where green and blue clipped in a cloud, both rise to red; where blue's clip level is
/// above red (it is 1.29 × green's on the Canon R8), blue stays there and green settles between. Only ever raises;
/// where every channel clipped, all go to the brightest.
/// <para>
/// Measured on the R8 sample's sky (Highlights darkening on scene values shows what the base curve's shoulder hid):
/// the earlier rule, the plain average of the unclipped channels only, rebuilt cloud edges lavender (a blue just under
/// its clip counted as clipped, so green came from red alone) or peach. A colour taken from the unclipped neighbourhood
/// was tried and dropped: the brightest unclipped pixels next to a cloud are magenta (below), so the clouds came out
/// pink, and the darker ones are the blue sky.
/// </para>
/// <para>
/// A channel counts as clipped also within <see cref="Reach"/> pixels of where it clipped, on pixels that are bright
/// themselves: demosaicing fills in a clipped green from its clipped neighbours, so next to a clipped cloud the green
/// comes out under the true value while red and blue are right. Taken at face value those pixels are magenta (a pink
/// band along the cloud edges). Dark pixels next to a clip (leaves against a cloud) are left alone.
/// </para>
/// </summary>
public sealed class HighlightReconstruction
{
    /// <summary>Where the rebuild starts blending in (share of the clip level; demosaicing smears the clip a little).</summary>
    public const float BlendFrom = 0.8f, BlendTo = 0.97f;

    /// <summary>How far (pixels) a clip spreads to bright neighbours: the reach of the demosaicing's green interpolation.</summary>
    public const int Reach = 2;

    /// <summary>A pixel takes on its neighbours' clipping from this share of the clip (its brightest channel) up.</summary>
    private const float SpreadFrom = 0.5f, SpreadTo = 0.8f;

    /// <summary>Gauss-Seidel passes for the joint rebuild (two clipped channels converge to within 0.1 % of the third).</summary>
    private const int Passes = 6;

    private readonly float[] _clip;

    /// <summary>Per pixel and channel: the largest clip level share within <see cref="Reach"/>, 0..255 over BlendFrom..BlendTo.</summary>
    private readonly byte[] _near;
    private readonly int _width;

    private HighlightReconstruction(float[] clip, byte[] near, int width)
    {
        _clip = clip;
        _near = near;
        _width = width;
    }

    /// <summary>For an image (interleaved camera RGB, <paramref name="width"/> × <paramref name="height"/>).</summary>
    public static HighlightReconstruction Build(ReadOnlySpan<ushort> rgb, int width, int height, float[] clip)
    {
        int n = width * 3;
        var level = new byte[(long)width * height * 3 is var size && size <= int.MaxValue ? (int)size : throw new OverflowException()];
        float scale0 = 255f / (clip[0] * (BlendTo - BlendFrom)), scale1 = 255f / (clip[1] * (BlendTo - BlendFrom)),
            scale2 = 255f / (clip[2] * (BlendTo - BlendFrom));
        float from0 = BlendFrom * clip[0], from1 = BlendFrom * clip[1], from2 = BlendFrom * clip[2];
        unsafe
        {
            fixed (ushort* source = rgb)
            {
                nint ptr = (nint)source;
                // own level per channel, then the largest along the row within Reach
                Parallel.For(0, height, y =>
                {
                    ushort* row = (ushort*)ptr + (long)y * n;
                    Span<byte> own = n <= 32768 ? stackalloc byte[n] : new byte[n];
                    for (int i = 0; i < n; i += 3)
                    {
                        own[i] = (byte)Math.Clamp((row[i] - from0) * scale0, 0f, 255f);
                        own[i + 1] = (byte)Math.Clamp((row[i + 1] - from1) * scale1, 0f, 255f);
                        own[i + 2] = (byte)Math.Clamp((row[i + 2] - from2) * scale2, 0f, 255f);
                    }
                    int o = y * n;
                    for (int x = 0; x < width; x++)
                    {
                        int x0 = Math.Max(0, x - Reach), x1 = Math.Min(width - 1, x + Reach);
                        for (int c = 0; c < 3; c++)
                        {
                            byte m = 0;
                            for (int k = x0; k <= x1; k++)
                                m = Math.Max(m, own[k * 3 + c]);
                            level[o + x * 3 + c] = m;
                        }
                    }
                });
            }
        }
        // then along the columns (in place, a band of rows at a time keeps the originals it still needs)
        var near = new byte[level.Length];
        Parallel.For(0, height, y =>
        {
            int y0 = Math.Max(0, y - Reach), y1 = Math.Min(height - 1, y + Reach);
            int o = y * n;
            for (int i = 0; i < n; i++)
            {
                byte m = 0;
                for (int k = y0; k <= y1; k++)
                    m = Math.Max(m, level[k * n + i]);
                near[o + i] = m;
            }
        });
        return new HighlightReconstruction(clip, near, width);
    }

    /// <summary>Rebuilds the clipped channels of the pixel at (<paramref name="x"/>, <paramref name="y"/>).</summary>
    public void Apply(Span<float> rgb, int x, int y)
    {
        int i = (y * _width + x) * 3;
        Apply(rgb, _clip, _near[i] / 255f, _near[i + 1] / 255f, _near[i + 2] / 255f);
    }

    /// <summary>
    /// Rebuilds the clipped channels of one pixel. <paramref name="nearR"/> … : how clipped each channel is close by
    /// (0..1 over <see cref="BlendFrom"/>..<see cref="BlendTo"/> of the clip level), counted on bright pixels. Blended in
    /// from <see cref="BlendFrom"/> of the clip level; only ever raises; where every channel clipped, all go to the
    /// brightest.
    /// </summary>
    public static void Apply(Span<float> rgb, ReadOnlySpan<float> clip, float nearR, float nearG, float nearB)
    {
        Span<float> t = stackalloc float[3];
        float brightest = 0, top = 0;
        for (int c = 0; c < 3; c++)
        {
            brightest = MathF.Max(brightest, rgb[c]);
            top = MathF.Max(top, rgb[c] / clip[c]);
        }
        float spread = Math.Clamp((top - SpreadFrom) / (SpreadTo - SpreadFrom), 0f, 1f);
        spread *= spread * (3 - 2 * spread);
        float unclipped = 0;
        bool any = false;
        for (int c = 0; c < 3; c++)
        {
            float f = Math.Clamp((rgb[c] / clip[c] - BlendFrom) / (BlendTo - BlendFrom), 0f, 1f);
            f = MathF.Max(f, spread * (c == 0 ? nearR : c == 1 ? nearG : nearB));
            t[c] = f * f * (3 - 2 * f);
            any |= t[c] > 0;
            unclipped += 1 - t[c];
        }
        if (!any)
            return;
        // Each clipped channel rises towards the average of the other two as rebuilt (solved together: two clipped
        // channels end at the unclipped one, or a clipped one stays at its own clip level where that is higher);
        // where (nearly) every channel clipped, towards the brightest.
        float others = Math.Min(unclipped, 1f);
        Span<float> x = [rgb[0], rgb[1], rgb[2]];
        for (int pass = 0; pass < Passes; pass++)
            for (int c = 0; c < 3; c++)
            {
                float target = others * (x[(c + 1) % 3] + x[(c + 2) % 3]) / 2 + (1 - others) * brightest;
                x[c] = target > rgb[c] ? rgb[c] + t[c] * (target - rgb[c]) : rgb[c];
            }
        x.CopyTo(rgb);
    }
}
