namespace PhotoEditor.Core.Imaging;

/// <summary>
/// Demosaicing of a Bayer mosaic in C#, for the methods the bundled LibRaw does not have. The mosaic is LibRaw's own
/// data just before its interpolation step (black subtracted, white balanced, 0..65535), so only the interpolation
/// differs from LibRaw's methods. <paramref name="pattern"/> gives the colour (0 = R, 1 = G, 2 = B) at
/// (row &amp; 1, column &amp; 1) as pattern[(row &amp; 1) * 2 + (column &amp; 1)].
/// <para>
/// RCD (Ratio Corrected Demosaicing, Luis Sanz Rodríguez, tiled by Ingo Weyrich) and AMaZE (Aliasing Minimization and
/// Zipper Elimination, Emil Martinec, optimised by Ingo Weyrich) are ports of RawTherapee's rcd_demosaic.cc and
/// amaze_demosaic_RT.cc (GPL 3; the scalar code paths, same tiling and constants). The app is for the owner's personal
/// use only, so the GPL code may be used here.
/// </para>
/// </summary>
public static partial class BayerDemosaic
{
    private static int Fc(byte[] pattern, int row, int col) => pattern[((row & 1) << 1) | (col & 1)];

    private static float Median(float a, float b, float c) => MathF.Max(MathF.Min(a, b), MathF.Min(MathF.Max(a, b), c));

    private static float Intp(float a, float b, float c) => a * (b - c) + c;

    private static float Sqr(float x) => x * x;

    /// <summary>Plain bilinear interpolation (for checking the hand-over from LibRaw against its own Linear method).</summary>
    public static void Bilinear(float[] cfa, int width, int height, byte[] pattern, float[] red, float[] green, float[] blue)
    {
        var planes = new[] { red, green, blue };
        Parallel.For(0, height, y =>
        {
            Span<float> sum = stackalloc float[3];
            Span<int> count = stackalloc int[3];
            for (int x = 0; x < width; x++)
            {
                sum.Clear();
                count.Clear();
                for (int dy = -1; dy <= 1; dy++)
                {
                    int yy = y + dy;
                    if (yy < 0 || yy >= height) continue;
                    for (int dx = -1; dx <= 1; dx++)
                    {
                        int xx = x + dx;
                        if (xx < 0 || xx >= width) continue;
                        int c = Fc(pattern, yy, xx);
                        sum[c] += cfa[yy * width + xx];
                        count[c]++;
                    }
                }
                int own = Fc(pattern, y, x);
                for (int c = 0; c < 3; c++)
                    planes[c][y * width + x] = c == own ? cfa[y * width + x] : count[c] > 0 ? sum[c] / count[c] : 0f;
            }
        });
    }

    /// <summary>Fills the outer <paramref name="border"/> pixels by averaging each colour over the 3 × 3 neighbourhood (dcraw's border_interpolate).</summary>
    private static void BorderInterpolate(float[] cfa, int width, int height, byte[] pattern, int border, float[] red, float[] green, float[] blue)
    {
        var planes = new[] { red, green, blue };
        Parallel.For(0, height, y =>
        {
            Span<float> sum = stackalloc float[3];
            Span<int> count = stackalloc int[3];
            for (int x = 0; x < width; x++)
            {
                if (x == border && y >= border && y < height - border)
                    x = width - border;
                if (x >= width) break;
                sum.Clear();
                count.Clear();
                for (int yy = y - 1; yy <= y + 1; yy++)
                    for (int xx = x - 1; xx <= x + 1; xx++)
                        if (yy >= 0 && yy < height && xx >= 0 && xx < width)
                        {
                            int c = Fc(pattern, yy, xx);
                            sum[c] += cfa[yy * width + xx];
                            count[c]++;
                        }
                int own = Fc(pattern, y, x);
                for (int c = 0; c < 3; c++)
                    planes[c][y * width + x] = c == own ? cfa[y * width + x] : count[c] > 0 ? sum[c] / count[c] : 0f;
            }
        });
    }

    // ---------------------------------------------------------------------------------------------------------------
    // RCD

    /// <summary>RCD demosaicing (RawTherapee's tiled implementation, 194 px tiles with a 9 px border).</summary>
    public static void Rcd(float[] raw, int width, int height, byte[] pattern, float[] red, float[] green, float[] blue)
    {
        const int tileBorder = 9, rcdBorder = 9, tileSize = 194, tileSizeN = tileSize - 2 * tileBorder;
        const int w1 = tileSize, w2 = 2 * tileSize, w3 = 3 * tileSize, w4 = 4 * tileSize;
        const float eps = 1e-5f, epssq = 1e-10f, scale = 65536f;
        int numTh = height / tileSizeN + (height % tileSizeN != 0 ? 1 : 0);
        int numTw = width / tileSizeN + (width % tileSizeN != 0 ? 1 : 0);

        Parallel.For(0, numTh * numTw,
            () => new RcdBuffers(tileSize),
            (tile, _, b) =>
            {
                int tr = tile / numTw, tc = tile % numTw;
                var cfa = b.Cfa;
                var rgb = b.Rgb;
                var vhDir = b.VhDir;
                var pqDir = b.PqDir;
                var lpf = b.Lpf;
                var pHpf = b.PHpf;
                var qHpf = b.QHpf;
                int rowStart = tr * tileSizeN;
                int rowEnd = Math.Min(rowStart + tileSize, height);
                if (rowStart + tileBorder == rowEnd - tileBorder) return b;
                int colStart = tc * tileSizeN;
                int colEnd = Math.Min(colStart + tileSize, width);
                if (colStart + tileBorder == colEnd - tileBorder) return b;
                int tileRows = Math.Min(rowEnd - rowStart, tileSize);
                int tilecols = Math.Min(colEnd - colStart, tileSize);

                for (int row = rowStart; row < rowEnd; row++)
                {
                    int c0 = Fc(pattern, row, colStart), c1 = Fc(pattern, row, colStart + 1);
                    for (int col = colStart, indx = (row - rowStart) * tileSize; col < colEnd; ++col, ++indx)
                    {
                        float v = Math.Clamp(raw[row * width + col] / scale, 0f, 1f);
                        cfa[indx] = rgb[c0][indx] = rgb[c1][indx] = v;
                    }
                }

                // Step 1: cardinal and diagonal interpolation directions
                var bufferV = b.BufferV;
                for (int row = 3; row < Math.Min(tileRows - 3, 5); ++row)
                    for (int col = 4, indx = row * tileSize + col; col < tilecols - 4; ++col, ++indx)
                        bufferV[row - 3][col - 4] = Sqr((cfa[indx - w3] - cfa[indx - w1] - cfa[indx + w1] + cfa[indx + w3])
                            - 3f * (cfa[indx - w2] + cfa[indx + w2]) + 6f * cfa[indx]);

                var bufferH = b.BufferH;
                float[] v0 = bufferV[0], v1 = bufferV[1], v2 = bufferV[2];
                for (int row = 4; row < tileRows - 4; ++row)
                {
                    for (int col = 3, indx = row * tileSize + col; col < tilecols - 3; ++col, ++indx)
                        bufferH[col - 3] = Sqr((cfa[indx - 3] - cfa[indx - 1] - cfa[indx + 1] + cfa[indx + 3])
                            - 3f * (cfa[indx - 2] + cfa[indx + 2]) + 6f * cfa[indx]);
                    for (int col = 4, indx = (row + 1) * tileSize + col; col < tilecols - 4; ++col, ++indx)
                        v2[col - 4] = Sqr((cfa[indx - w3] - cfa[indx - w1] - cfa[indx + w1] + cfa[indx + w3])
                            - 3f * (cfa[indx - w2] + cfa[indx + w2]) + 6f * cfa[indx]);
                    for (int col = 4, indx = row * tileSize + col; col < tilecols - 4; ++col, ++indx)
                    {
                        float vStat = MathF.Max(epssq, v0[col - 4] + v1[col - 4] + v2[col - 4]);
                        float hStat = MathF.Max(epssq, bufferH[col - 4] + bufferH[col - 3] + bufferH[col - 2]);
                        vhDir[indx] = vStat / (vStat + hStat);
                    }
                    // rotate row0, row1, row2 to row1, row2, row0
                    (v0, v2) = (v2, v0);
                    (v0, v1) = (v1, v0);
                }

                // Step 2: low pass filter of the raw data at the red and blue positions
                for (int row = 2; row < tileRows - 2; ++row)
                    for (int col = 2 + (Fc(pattern, row, 0) & 1), indx = row * tileSize + col, lpindx = indx / 2; col < tilecols - 2; col += 2, indx += 2, ++lpindx)
                        lpf[lpindx] = cfa[indx]
                            + 0.5f * (cfa[indx - w1] + cfa[indx + w1] + cfa[indx - 1] + cfa[indx + 1])
                            + 0.25f * (cfa[indx - w1 - 1] + cfa[indx - w1 + 1] + cfa[indx + w1 - 1] + cfa[indx + w1 + 1]);

                // Step 3: green at the red and blue positions
                for (int row = 4; row < tileRows - 4; ++row)
                    for (int col = 4 + (Fc(pattern, row, 0) & 1), indx = row * tileSize + col, lpindx = indx / 2; col < tilecols - 4; col += 2, indx += 2, ++lpindx)
                    {
                        float cfai = cfa[indx];
                        float nGrad = eps + (MathF.Abs(cfa[indx - w1] - cfa[indx + w1]) + MathF.Abs(cfai - cfa[indx - w2])) + (MathF.Abs(cfa[indx - w1] - cfa[indx - w3]) + MathF.Abs(cfa[indx - w2] - cfa[indx - w4]));
                        float sGrad = eps + (MathF.Abs(cfa[indx - w1] - cfa[indx + w1]) + MathF.Abs(cfai - cfa[indx + w2])) + (MathF.Abs(cfa[indx + w1] - cfa[indx + w3]) + MathF.Abs(cfa[indx + w2] - cfa[indx + w4]));
                        float wGrad = eps + (MathF.Abs(cfa[indx - 1] - cfa[indx + 1]) + MathF.Abs(cfai - cfa[indx - 2])) + (MathF.Abs(cfa[indx - 1] - cfa[indx - 3]) + MathF.Abs(cfa[indx - 2] - cfa[indx - 4]));
                        float eGrad = eps + (MathF.Abs(cfa[indx - 1] - cfa[indx + 1]) + MathF.Abs(cfai - cfa[indx + 2])) + (MathF.Abs(cfa[indx + 1] - cfa[indx + 3]) + MathF.Abs(cfa[indx + 2] - cfa[indx + 4]));

                        float lpfi = lpf[lpindx];
                        float nEst = cfa[indx - w1] * (lpfi + lpfi) / (eps + lpfi + lpf[lpindx - w1]);
                        float sEst = cfa[indx + w1] * (lpfi + lpfi) / (eps + lpfi + lpf[lpindx + w1]);
                        float wEst = cfa[indx - 1] * (lpfi + lpfi) / (eps + lpfi + lpf[lpindx - 1]);
                        float eEst = cfa[indx + 1] * (lpfi + lpfi) / (eps + lpfi + lpf[lpindx + 1]);

                        float vEst = (sGrad * nEst + nGrad * sEst) / (nGrad + sGrad);
                        float hEst = (wGrad * eEst + eGrad * wEst) / (eGrad + wGrad);

                        float vhCentral = vhDir[indx];
                        float vhNeighbourhood = 0.25f * ((vhDir[indx - w1 - 1] + vhDir[indx - w1 + 1]) + (vhDir[indx + w1 - 1] + vhDir[indx + w1 + 1]));
                        float vhDisc = MathF.Abs(0.5f - vhCentral) < MathF.Abs(0.5f - vhNeighbourhood) ? vhNeighbourhood : vhCentral;
                        rgb[1][indx] = Intp(vhDisc, hEst, vEst);
                    }

                // Step 4.0: square of the P / Q diagonal colour difference high pass filter
                for (int row = 3; row < tileRows - 3; ++row)
                    for (int col = 3, indx = row * tileSize + col, indx2 = indx / 2; col < tilecols - 3; col += 2, indx += 2, indx2++)
                    {
                        pHpf[indx2] = Sqr((cfa[indx - w3 - 3] - cfa[indx - w1 - 1] - cfa[indx + w1 + 1] + cfa[indx + w3 + 3]) - 3f * (cfa[indx - w2 - 2] + cfa[indx + w2 + 2]) + 6f * cfa[indx]);
                        qHpf[indx2] = Sqr((cfa[indx - w3 + 3] - cfa[indx - w1 + 1] - cfa[indx + w1 - 1] + cfa[indx + w3 - 3]) - 3f * (cfa[indx - w2 + 2] + cfa[indx + w2 - 2]) + 6f * cfa[indx]);
                    }

                // Step 4.1: P / Q diagonal discrimination strength
                for (int row = 4; row < tileRows - 4; ++row)
                    for (int col = 4 + (Fc(pattern, row, 0) & 1), indx = row * tileSize + col, indx2 = indx / 2, indx3 = (indx - w1 - 1) / 2, indx4 = (indx + w1 - 1) / 2; col < tilecols - 4; col += 2, indx += 2, indx2++, indx3++, indx4++)
                    {
                        float pStat = MathF.Max(epssq, pHpf[indx3] + pHpf[indx2] + pHpf[indx4 + 1]);
                        float qStat = MathF.Max(epssq, qHpf[indx3 + 1] + qHpf[indx2] + qHpf[indx4]);
                        pqDir[indx2] = pStat / (pStat + qStat);
                    }

                // Step 4.2: red and blue at the blue and red positions
                for (int row = 4; row < tileRows - 4; ++row)
                    for (int col = 4 + (Fc(pattern, row, 0) & 1), indx = row * tileSize + col, c = 2 - Fc(pattern, row, col), pqindx = indx / 2, pqindx2 = (indx - w1 - 1) / 2, pqindx3 = (indx + w1 - 1) / 2; col < tilecols - 4; col += 2, indx += 2, ++pqindx, ++pqindx2, ++pqindx3)
                    {
                        float pqCentral = pqDir[pqindx];
                        float pqNeighbourhood = 0.25f * (pqDir[pqindx2] + pqDir[pqindx2 + 1] + pqDir[pqindx3] + pqDir[pqindx3 + 1]);
                        float pqDisc = MathF.Abs(0.5f - pqCentral) < MathF.Abs(0.5f - pqNeighbourhood) ? pqNeighbourhood : pqCentral;
                        var rc = rgb[c];
                        var g = rgb[1];
                        float nwGrad = eps + MathF.Abs(rc[indx - w1 - 1] - rc[indx + w1 + 1]) + MathF.Abs(rc[indx - w1 - 1] - rc[indx - w3 - 3]) + MathF.Abs(g[indx] - g[indx - w2 - 2]);
                        float neGrad = eps + MathF.Abs(rc[indx - w1 + 1] - rc[indx + w1 - 1]) + MathF.Abs(rc[indx - w1 + 1] - rc[indx - w3 + 3]) + MathF.Abs(g[indx] - g[indx - w2 + 2]);
                        float swGrad = eps + MathF.Abs(rc[indx - w1 + 1] - rc[indx + w1 - 1]) + MathF.Abs(rc[indx + w1 - 1] - rc[indx + w3 - 3]) + MathF.Abs(g[indx] - g[indx + w2 - 2]);
                        float seGrad = eps + MathF.Abs(rc[indx - w1 - 1] - rc[indx + w1 + 1]) + MathF.Abs(rc[indx + w1 + 1] - rc[indx + w3 + 3]) + MathF.Abs(g[indx] - g[indx + w2 + 2]);
                        float nwEst = rc[indx - w1 - 1] - g[indx - w1 - 1];
                        float neEst = rc[indx - w1 + 1] - g[indx - w1 + 1];
                        float swEst = rc[indx + w1 - 1] - g[indx + w1 - 1];
                        float seEst = rc[indx + w1 + 1] - g[indx + w1 + 1];
                        float pEst = (nwGrad * seEst + seGrad * nwEst) / (nwGrad + seGrad);
                        float qEst = (neGrad * swEst + swGrad * neEst) / (neGrad + swGrad);
                        rc[indx] = g[indx] + Intp(pqDisc, qEst, pEst);
                    }

                // Step 4.3: red and blue at the green positions
                for (int row = 4; row < tileRows - 4; ++row)
                    for (int col = 4 + (Fc(pattern, row, 1) & 1), indx = row * tileSize + col; col < tilecols - 4; col += 2, indx += 2)
                    {
                        float vhCentral = vhDir[indx];
                        float vhNeighbourhood = 0.25f * ((vhDir[indx - w1 - 1] + vhDir[indx - w1 + 1]) + (vhDir[indx + w1 - 1] + vhDir[indx + w1 + 1]));
                        float vhDisc = MathF.Abs(0.5f - vhCentral) < MathF.Abs(0.5f - vhNeighbourhood) ? vhNeighbourhood : vhCentral;
                        var g = rgb[1];
                        float rgb1 = g[indx];
                        float n1 = eps + MathF.Abs(rgb1 - g[indx - w2]);
                        float s1 = eps + MathF.Abs(rgb1 - g[indx + w2]);
                        float wv1 = eps + MathF.Abs(rgb1 - g[indx - 2]);
                        float e1 = eps + MathF.Abs(rgb1 - g[indx + 2]);
                        float rgb1mw1 = g[indx - w1], rgb1pw1 = g[indx + w1], rgb1m1 = g[indx - 1], rgb1p1 = g[indx + 1];
                        for (int c = 0; c <= 2; c += 2)
                        {
                            var rc = rgb[c];
                            float snAbs = MathF.Abs(rc[indx - w1] - rc[indx + w1]);
                            float ewAbs = MathF.Abs(rc[indx - 1] - rc[indx + 1]);
                            float nGrad = n1 + snAbs + MathF.Abs(rc[indx - w1] - rc[indx - w3]);
                            float sGrad = s1 + snAbs + MathF.Abs(rc[indx + w1] - rc[indx + w3]);
                            float wGrad = wv1 + ewAbs + MathF.Abs(rc[indx - 1] - rc[indx - 3]);
                            float eGrad = e1 + ewAbs + MathF.Abs(rc[indx + 1] - rc[indx + 3]);
                            float nEst = rc[indx - w1] - rgb1mw1;
                            float sEst = rc[indx + w1] - rgb1pw1;
                            float wEst = rc[indx - 1] - rgb1m1;
                            float eEst = rc[indx + 1] - rgb1p1;
                            float vEst = (nGrad * sEst + sGrad * nEst) / (nGrad + sGrad);
                            float hEst = (eGrad * wEst + wGrad * eEst) / (eGrad + wGrad);
                            rc[indx] = rgb1 + Intp(vhDisc, hEst, vEst);
                        }
                    }

                int firstVertical = rowStart + (tr == 0 ? rcdBorder : tileBorder);
                int lastVertical = rowEnd - (tr == numTh - 1 ? rcdBorder : tileBorder);
                int firstHorizontal = colStart + (tc == 0 ? rcdBorder : tileBorder);
                int lastHorizontal = colEnd - (tc == numTw - 1 ? rcdBorder : tileBorder);
                for (int row = firstVertical; row < lastVertical; ++row)
                    for (int col = firstHorizontal; col < lastHorizontal; ++col)
                    {
                        int idx = (row - rowStart) * tileSize + col - colStart;
                        red[row * width + col] = MathF.Max(0f, rgb[0][idx] * scale);
                        green[row * width + col] = MathF.Max(0f, rgb[1][idx] * scale);
                        blue[row * width + col] = MathF.Max(0f, rgb[2][idx] * scale);
                    }
                return b;
            },
            _ => { });

        BorderInterpolate(raw, width, height, pattern, rcdBorder, red, green, blue);
    }

    private sealed class RcdBuffers(int tileSize)
    {
        public readonly float[] Cfa = new float[tileSize * tileSize];
        public readonly float[][] Rgb = [new float[tileSize * tileSize], new float[tileSize * tileSize], new float[tileSize * tileSize]];
        public readonly float[] VhDir = new float[tileSize * tileSize];
        public readonly float[] PqDir = new float[tileSize * tileSize / 2];
        public readonly float[] Lpf = new float[tileSize * tileSize / 2];
        public readonly float[] PHpf = new float[tileSize * tileSize / 2];
        public readonly float[] QHpf = new float[tileSize * tileSize / 2];
        public readonly float[][] BufferV = [new float[tileSize - 8], new float[tileSize - 8], new float[tileSize - 8]];
        public readonly float[] BufferH = new float[tileSize - 6];
    }
}
