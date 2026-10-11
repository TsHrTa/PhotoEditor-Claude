namespace PhotoEditor.Core.Imaging;

public static partial class BayerDemosaic
{
    /// <summary>
    /// AMaZE demosaicing (RawTherapee's amaze_demosaic_RT.cc, scalar path: 160 px tiles overlapping by 32, the image
    /// mirrored 16 px beyond its edges). <paramref name="clipPoint"/> is where the lowest channel clips (0..65535): near
    /// it the method falls back to Hamilton-Adams instead of colour ratios.
    /// </summary>
    public static void Amaze(float[] raw, int width, int height, byte[] pattern, float clipPoint, float[] red, float[] green, float[] blue)
    {
        const int ts = 160, tsh = ts / 2;
        const int v1 = ts, v2 = 2 * ts, v3 = 3 * ts, p1 = -ts + 1, p2 = -2 * ts + 2, p3 = -3 * ts + 3, m1 = ts + 1, m2 = 2 * ts + 2, m3 = 3 * ts + 3;
        const float eps = 1e-5f, epssq = 1e-10f, arthresh = 0.75f, nyqthresh = 0.5f;
        float[] gaussodd = [0.14659727707323927f, 0.103592713382435f, 0.0732036125103057f, 0.0365543548389495f];
        float[] gaussgrad =
        [
            nyqthresh * 0.07384411893421103f, nyqthresh * 0.06207511968171489f, nyqthresh * 0.0521818194747806f,
            nyqthresh * 0.03687419286733595f, nyqthresh * 0.03099732204057846f, nyqthresh * 0.018413194161458882f,
        ];
        float[] gausseven = [0.13719494435797422f, 0.05640252782101291f];
        float[] gquinc = [0.169917f, 0.108947f, 0.069855f, 0.0287182f];
        float clipPt = clipPoint / 65535f, clipPt8 = 0.8f * clipPt;

        // (ey, ex): offset of the red pixel within a Bayer quartet
        int ex, ey;
        if (Fc(pattern, 0, 0) == 1)
        {
            if (Fc(pattern, 0, 1) == 0) { ey = 0; ex = 1; }
            else { ey = 1; ex = 0; }
        }
        else if (Fc(pattern, 0, 0) == 0) { ey = 0; ex = 0; }
        else { ey = 1; ex = 1; }

        var tiles = new List<(int Top, int Left)>();
        for (int top = -16; top < height; top += ts - 32)
            for (int left = -16; left < width; left += ts - 32)
                tiles.Add((top, left));

        float Raw(int row, int col) => raw[row * width + col] / 65535f;
        int Fcp(int row, int col) => Fc(pattern, row, col);

        Parallel.For(0, tiles.Count,
            () => new AmazeBuffers(ts),
            (t, _, b) =>
            {
                b.Clear();
                var (top, left) = tiles[t];
                var cfa = b.Cfa; var rgbgreen = b.RgbGreen; var delhvsqsum = b.DelHvSqSum; var dirwts0 = b.DirWts0; var dirwts1 = b.DirWts1;
                var vcd = b.Vcd; var hcd = b.Hcd; var vcdalt = b.VcdAlt; var hcdalt = b.HcdAlt; var cddiffsq = b.CdDiffSq; var hvwt = b.HvWt;
                var dgrb0 = b.Dgrb0; var dgrb1 = b.Dgrb1; var delp = b.DelP; var delm = b.DelM; var rbint = b.RbInt;
                var dgrb2h = b.Dgrb2H; var dgrb2v = b.Dgrb2V; var dgintv = b.DgIntV; var dginth = b.DgIntH;
                var dgrbsq1m = b.DgrbSq1M; var dgrbsq1p = b.DgrbSq1P; var pmwt = b.PmWt; var rbm = b.Rbm; var rbp = b.Rbp;
                var nyquist = b.Nyquist; var nyquist2 = b.Nyquist2; var nyqutest = b.NyquTest;

                int bottom = Math.Min(top + ts, height + 16);
                int right = Math.Min(left + ts, width + 16);
                int rr1 = bottom - top;
                int cc1 = right - left;
                int rrmin = top < 0 ? 16 : 0;
                int ccmin = left < 0 ? 16 : 0;
                int rrmax = bottom > height ? height - top : rr1;
                int ccmax = right > width ? width - left : cc1;

                // tile initialisation (borders mirrored)
                if (rrmin > 0)
                    for (int rr = 0; rr < 16; rr++)
                        for (int cc = ccmin, row = 32 - rr + top; cc < ccmax; cc++)
                            cfa[rr * ts + cc] = Raw(row, cc + left);
                for (int rr = rrmin; rr < rrmax; rr++)
                {
                    int row = rr + top;
                    for (int cc = ccmin; cc < ccmax; cc++)
                        cfa[rr * ts + cc] = Raw(row, cc + left);
                }
                if (rrmax < rr1)
                    for (int rr = 0; rr < 16; rr++)
                        for (int cc = ccmin; cc < ccmax; cc++)
                            cfa[(rrmax + rr) * ts + cc] = Raw(height - rr - 2, left + cc);
                if (ccmin > 0)
                    for (int rr = rrmin; rr < rrmax; rr++)
                        for (int cc = 0, row = rr + top; cc < 16; cc++)
                            cfa[rr * ts + cc] = Raw(row, 32 - cc + left);
                if (ccmax < cc1)
                    for (int rr = rrmin; rr < rrmax; rr++)
                        for (int cc = 0; cc < 16; cc++)
                            cfa[rr * ts + ccmax + cc] = Raw(top + rr, width - cc - 2);
                if (rrmin > 0 && ccmin > 0)
                    for (int rr = 0; rr < 16; rr++)
                        for (int cc = 0; cc < 16; cc++)
                            cfa[rr * ts + cc] = Raw(32 - rr, 32 - cc);
                if (rrmax < rr1 && ccmax < cc1)
                    for (int rr = 0; rr < 16; rr++)
                        for (int cc = 0; cc < 16; cc++)
                            cfa[(rrmax + rr) * ts + ccmax + cc] = Raw(height - rr - 2, width - cc - 2);
                if (rrmin > 0 && ccmax < cc1)
                    for (int rr = 0; rr < 16; rr++)
                        for (int cc = 0; cc < 16; cc++)
                            cfa[rr * ts + ccmax + cc] = Raw(32 - rr, width - cc - 2);
                if (rrmax < rr1 && ccmin > 0)
                    for (int rr = 0; rr < 16; rr++)
                        for (int cc = 0; cc < 16; cc++)
                            cfa[(rrmax + rr) * ts + cc] = Raw(height - rr - 2, 32 - cc);
                Array.Copy(cfa, rgbgreen, cfa.Length);

                // horizontal and vertical gradients
                for (int rr = 2; rr < rr1 - 2; rr++)
                    for (int cc = 2, indx = rr * ts + cc; cc < cc1 - 2; cc++, indx++)
                    {
                        float delh = MathF.Abs(cfa[indx + 1] - cfa[indx - 1]);
                        float delv = MathF.Abs(cfa[indx + v1] - cfa[indx - v1]);
                        dirwts0[indx] = eps + MathF.Abs(cfa[indx + v2] - cfa[indx]) + MathF.Abs(cfa[indx] - cfa[indx - v2]) + delv;
                        dirwts1[indx] = eps + MathF.Abs(cfa[indx + 2] - cfa[indx]) + MathF.Abs(cfa[indx] - cfa[indx - 2]) + delh;
                        delhvsqsum[indx] = Sqr(delh) + Sqr(delv);
                    }

                // interpolate vertical and horizontal colour differences
                for (int rr = 4; rr < rr1 - 4; rr++)
                {
                    bool fcswitch = (Fcp(rr, 4) & 1) != 0;
                    for (int cc = 4, indx = rr * ts + cc; cc < cc1 - 4; cc++, indx++)
                    {
                        float cru = cfa[indx - v1] * (dirwts0[indx - v2] + dirwts0[indx]) / (dirwts0[indx - v2] * (eps + cfa[indx]) + dirwts0[indx] * (eps + cfa[indx - v2]));
                        float crd = cfa[indx + v1] * (dirwts0[indx + v2] + dirwts0[indx]) / (dirwts0[indx + v2] * (eps + cfa[indx]) + dirwts0[indx] * (eps + cfa[indx + v2]));
                        float crl = cfa[indx - 1] * (dirwts1[indx - 2] + dirwts1[indx]) / (dirwts1[indx - 2] * (eps + cfa[indx]) + dirwts1[indx] * (eps + cfa[indx - 2]));
                        float crr = cfa[indx + 1] * (dirwts1[indx + 2] + dirwts1[indx]) / (dirwts1[indx + 2] * (eps + cfa[indx]) + dirwts1[indx] * (eps + cfa[indx + 2]));

                        float guha = cfa[indx - v1] + 0.5f * (cfa[indx] - cfa[indx - v2]);
                        float gdha = cfa[indx + v1] + 0.5f * (cfa[indx] - cfa[indx + v2]);
                        float glha = cfa[indx - 1] + 0.5f * (cfa[indx] - cfa[indx - 2]);
                        float grha = cfa[indx + 1] + 0.5f * (cfa[indx] - cfa[indx + 2]);

                        float guar = MathF.Abs(1f - cru) < arthresh ? cfa[indx] * cru : guha;
                        float gdar = MathF.Abs(1f - crd) < arthresh ? cfa[indx] * crd : gdha;
                        float glar = MathF.Abs(1f - crl) < arthresh ? cfa[indx] * crl : glha;
                        float grar = MathF.Abs(1f - crr) < arthresh ? cfa[indx] * crr : grha;

                        float hwt = dirwts1[indx - 1] / (dirwts1[indx - 1] + dirwts1[indx + 1]);
                        float vwt = dirwts0[indx - v1] / (dirwts0[indx + v1] + dirwts0[indx - v1]);

                        float gintvha = vwt * gdha + (1f - vwt) * guha;
                        float ginthha = hwt * grha + (1f - hwt) * glha;

                        if (fcswitch)
                        {
                            vcd[indx] = cfa[indx] - (vwt * gdar + (1f - vwt) * guar);
                            hcd[indx] = cfa[indx] - (hwt * grar + (1f - hwt) * glar);
                            vcdalt[indx] = cfa[indx] - gintvha;
                            hcdalt[indx] = cfa[indx] - ginthha;
                        }
                        else
                        {
                            vcd[indx] = (vwt * gdar + (1f - vwt) * guar) - cfa[indx];
                            hcd[indx] = (hwt * grar + (1f - hwt) * glar) - cfa[indx];
                            vcdalt[indx] = gintvha - cfa[indx];
                            hcdalt[indx] = ginthha - cfa[indx];
                        }
                        fcswitch = !fcswitch;

                        if (cfa[indx] > clipPt8 || gintvha > clipPt8 || ginthha > clipPt8)
                        {
                            // use Hamilton-Adams if the highlights are (nearly) clipped
                            guar = guha; gdar = gdha; glar = glha; grar = grha;
                            vcd[indx] = vcdalt[indx];
                            hcd[indx] = hcdalt[indx];
                        }

                        dgintv[indx] = MathF.Min(Sqr(guha - gdha), Sqr(guar - gdar));
                        dginth[indx] = MathF.Min(Sqr(glha - grha), Sqr(glar - grar));
                    }
                }

                for (int rr = 4; rr < rr1 - 4; rr++)
                {
                    bool c = (Fcp(rr, 4) & 1) != 0;
                    for (int cc = 4, indx = rr * ts + cc; cc < cc1 - 4; cc++, indx++)
                    {
                        float hcdvar = 3f * (Sqr(hcd[indx - 2]) + Sqr(hcd[indx]) + Sqr(hcd[indx + 2])) - Sqr(hcd[indx - 2] + hcd[indx] + hcd[indx + 2]);
                        float hcdaltvar = 3f * (Sqr(hcdalt[indx - 2]) + Sqr(hcdalt[indx]) + Sqr(hcdalt[indx + 2])) - Sqr(hcdalt[indx - 2] + hcdalt[indx] + hcdalt[indx + 2]);
                        float vcdvar = 3f * (Sqr(vcd[indx - v2]) + Sqr(vcd[indx]) + Sqr(vcd[indx + v2])) - Sqr(vcd[indx - v2] + vcd[indx] + vcd[indx + v2]);
                        float vcdaltvar = 3f * (Sqr(vcdalt[indx - v2]) + Sqr(vcdalt[indx]) + Sqr(vcdalt[indx + v2])) - Sqr(vcdalt[indx - v2] + vcdalt[indx] + vcdalt[indx + v2]);

                        if (hcdaltvar < hcdvar) hcd[indx] = hcdalt[indx];
                        if (vcdaltvar < vcdvar) vcd[indx] = vcdalt[indx];

                        float gintv, ginth;
                        if (c)
                        {
                            // G site
                            ginth = -hcd[indx] + cfa[indx];
                            gintv = -vcd[indx] + cfa[indx];
                            if (hcd[indx] > 0)
                            {
                                if (3f * hcd[indx] > ginth + cfa[indx])
                                    hcd[indx] = -Median(ginth, cfa[indx - 1], cfa[indx + 1]) + cfa[indx];
                                else
                                {
                                    float hwt = 1f - 3f * hcd[indx] / (eps + ginth + cfa[indx]);
                                    hcd[indx] = hwt * hcd[indx] + (1f - hwt) * (-Median(ginth, cfa[indx - 1], cfa[indx + 1]) + cfa[indx]);
                                }
                            }
                            if (vcd[indx] > 0)
                            {
                                if (3f * vcd[indx] > gintv + cfa[indx])
                                    vcd[indx] = -Median(gintv, cfa[indx - v1], cfa[indx + v1]) + cfa[indx];
                                else
                                {
                                    float vwt = 1f - 3f * vcd[indx] / (eps + gintv + cfa[indx]);
                                    vcd[indx] = vwt * vcd[indx] + (1f - vwt) * (-Median(gintv, cfa[indx - v1], cfa[indx + v1]) + cfa[indx]);
                                }
                            }
                            if (ginth > clipPt) hcd[indx] = -Median(ginth, cfa[indx - 1], cfa[indx + 1]) + cfa[indx];
                            if (gintv > clipPt) vcd[indx] = -Median(gintv, cfa[indx - v1], cfa[indx + v1]) + cfa[indx];
                        }
                        else
                        {
                            // R or B site
                            ginth = hcd[indx] + cfa[indx];
                            gintv = vcd[indx] + cfa[indx];
                            if (hcd[indx] < 0)
                            {
                                if (3f * hcd[indx] < -(ginth + cfa[indx]))
                                    hcd[indx] = Median(ginth, cfa[indx - 1], cfa[indx + 1]) - cfa[indx];
                                else
                                {
                                    float hwt = 1f + 3f * hcd[indx] / (eps + ginth + cfa[indx]);
                                    hcd[indx] = hwt * hcd[indx] + (1f - hwt) * (Median(ginth, cfa[indx - 1], cfa[indx + 1]) - cfa[indx]);
                                }
                            }
                            if (vcd[indx] < 0)
                            {
                                if (3f * vcd[indx] < -(gintv + cfa[indx]))
                                    vcd[indx] = Median(gintv, cfa[indx - v1], cfa[indx + v1]) - cfa[indx];
                                else
                                {
                                    float vwt = 1f + 3f * vcd[indx] / (eps + gintv + cfa[indx]);
                                    vcd[indx] = vwt * vcd[indx] + (1f - vwt) * (Median(gintv, cfa[indx - v1], cfa[indx + v1]) - cfa[indx]);
                                }
                            }
                            if (ginth > clipPt) hcd[indx] = Median(ginth, cfa[indx - 1], cfa[indx + 1]) - cfa[indx];
                            if (gintv > clipPt) vcd[indx] = Median(gintv, cfa[indx - v1], cfa[indx + v1]) - cfa[indx];
                            cddiffsq[indx] = Sqr(vcd[indx] - hcd[indx]);
                        }
                        c = !c;
                    }
                }

                for (int rr = 6; rr < rr1 - 6; rr++)
                    for (int cc = 6 + (Fcp(rr, 2) & 1), indx = rr * ts + cc; cc < cc1 - 6; cc += 2, indx += 2)
                    {
                        float uave = vcd[indx] + vcd[indx - v1] + vcd[indx - v2] + vcd[indx - v3];
                        float dave = vcd[indx] + vcd[indx + v1] + vcd[indx + v2] + vcd[indx + v3];
                        float lave = hcd[indx] + hcd[indx - 1] + hcd[indx - 2] + hcd[indx - 3];
                        float rave = hcd[indx] + hcd[indx + 1] + hcd[indx + 2] + hcd[indx + 3];

                        float dgrbvvaru = Sqr(vcd[indx] - uave) + Sqr(vcd[indx - v1] - uave) + Sqr(vcd[indx - v2] - uave) + Sqr(vcd[indx - v3] - uave);
                        float dgrbvvard = Sqr(vcd[indx] - dave) + Sqr(vcd[indx + v1] - dave) + Sqr(vcd[indx + v2] - dave) + Sqr(vcd[indx + v3] - dave);
                        float dgrbhvarl = Sqr(hcd[indx] - lave) + Sqr(hcd[indx - 1] - lave) + Sqr(hcd[indx - 2] - lave) + Sqr(hcd[indx - 3] - lave);
                        float dgrbhvarr = Sqr(hcd[indx] - rave) + Sqr(hcd[indx + 1] - rave) + Sqr(hcd[indx + 2] - rave) + Sqr(hcd[indx + 3] - rave);

                        float hwt = dirwts1[indx - 1] / (dirwts1[indx - 1] + dirwts1[indx + 1]);
                        float vwt = dirwts0[indx - v1] / (dirwts0[indx + v1] + dirwts0[indx - v1]);

                        float vcdvar = epssq + vwt * dgrbvvard + (1f - vwt) * dgrbvvaru;
                        float hcdvar = epssq + hwt * dgrbhvarr + (1f - hwt) * dgrbhvarl;

                        dgrbvvaru = dgintv[indx] + dgintv[indx - v1] + dgintv[indx - v2];
                        dgrbvvard = dgintv[indx] + dgintv[indx + v1] + dgintv[indx + v2];
                        dgrbhvarl = dginth[indx] + dginth[indx - 1] + dginth[indx - 2];
                        dgrbhvarr = dginth[indx] + dginth[indx + 1] + dginth[indx + 2];

                        float vcdvar1 = epssq + vwt * dgrbvvard + (1f - vwt) * dgrbvvaru;
                        float hcdvar1 = epssq + hwt * dgrbhvarr + (1f - hwt) * dgrbhvarl;

                        float varwt = hcdvar / (vcdvar + hcdvar);
                        float diffwt = hcdvar1 / (vcdvar1 + hcdvar1);

                        hvwt[indx >> 1] = (0.5f - varwt) * (0.5f - diffwt) > 0f && MathF.Abs(0.5f - diffwt) < MathF.Abs(0.5f - varwt) ? varwt : diffwt;
                    }

                // Nyquist texture test
                for (int rr = 6; rr < rr1 - 6; rr++)
                    for (int cc = 6 + (Fcp(rr, 2) & 1), indx = rr * ts + cc; cc < cc1 - 6; cc += 2, indx += 2)
                        nyqutest[indx >> 1] = (gaussodd[0] * cddiffsq[indx]
                                + gaussodd[1] * (cddiffsq[indx - m1] + cddiffsq[indx + p1] + cddiffsq[indx - p1] + cddiffsq[indx + m1])
                                + gaussodd[2] * (cddiffsq[indx - v2] + cddiffsq[indx - 2] + cddiffsq[indx + 2] + cddiffsq[indx + v2])
                                + gaussodd[3] * (cddiffsq[indx - m2] + cddiffsq[indx + p2] + cddiffsq[indx - p2] + cddiffsq[indx + m2]))
                            - (gaussgrad[0] * delhvsqsum[indx]
                                + gaussgrad[1] * (delhvsqsum[indx - v1] + delhvsqsum[indx + 1] + delhvsqsum[indx - 1] + delhvsqsum[indx + v1])
                                + gaussgrad[2] * (delhvsqsum[indx - m1] + delhvsqsum[indx + p1] + delhvsqsum[indx - p1] + delhvsqsum[indx + m1])
                                + gaussgrad[3] * (delhvsqsum[indx - v2] + delhvsqsum[indx - 2] + delhvsqsum[indx + 2] + delhvsqsum[indx + v2])
                                + gaussgrad[4] * (delhvsqsum[indx - v2 - 1] + delhvsqsum[indx - v2 + 1] + delhvsqsum[indx - ts - 2] + delhvsqsum[indx - ts + 2]
                                    + delhvsqsum[indx + ts - 2] + delhvsqsum[indx + ts + 2] + delhvsqsum[indx + v2 - 1] + delhvsqsum[indx + v2 + 1])
                                + gaussgrad[5] * (delhvsqsum[indx - m2] + delhvsqsum[indx + p2] + delhvsqsum[indx - p2] + delhvsqsum[indx + m2]));

                int nystartrow = 0, nyendrow = 0, nystartcol = ts + 1, nyendcol = 0;
                for (int rr = 6; rr < rr1 - 6; rr++)
                    for (int cc = 6 + (Fcp(rr, 2) & 1), indx = rr * ts + cc; cc < cc1 - 6; cc += 2, indx += 2)
                        if (nyqutest[indx >> 1] > 0f)
                        {
                            nyquist[indx >> 1] = 1;
                            nystartrow = nystartrow != 0 ? nystartrow : rr;
                            nyendrow = rr;
                            nystartcol = nystartcol > cc ? cc : nystartcol;
                            nyendcol = nyendcol < cc ? cc : nyendcol;
                        }

                bool doNyquist = nystartrow != nyendrow && nystartcol != nyendcol;
                if (doNyquist)
                {
                    nyendrow++;
                    nyendcol++;
                    nystartcol -= nystartcol & 1;
                    nystartrow = Math.Max(8, nystartrow);
                    nyendrow = Math.Min(rr1 - 8, nyendrow);
                    nystartcol = Math.Max(8, nystartcol);
                    nyendcol = Math.Min(cc1 - 8, nyendcol);

                    for (int rr = nystartrow; rr < nyendrow; rr++)
                        for (int indx = rr * ts + nystartcol + (Fcp(rr, 2) & 1); indx < rr * ts + nyendcol; indx += 2)
                        {
                            int nyquisttemp = nyquist[(indx - v2) >> 1] + nyquist[(indx - m1) >> 1] + nyquist[(indx + p1) >> 1]
                                + nyquist[(indx - 2) >> 1] + nyquist[(indx + 2) >> 1]
                                + nyquist[(indx - p1) >> 1] + nyquist[(indx + m1) >> 1] + nyquist[(indx + v2) >> 1];
                            nyquist2[indx >> 1] = nyquisttemp > 4 ? (byte)1 : nyquisttemp < 4 ? (byte)0 : nyquist[indx >> 1];
                        }

                    // area interpolation in Nyquist texture
                    for (int rr = nystartrow; rr < nyendrow; rr++)
                        for (int indx = rr * ts + nystartcol + (Fcp(rr, 2) & 1); indx < rr * ts + nyendcol; indx += 2)
                        {
                            if (nyquist2[indx >> 1] == 0) continue;
                            float sumcfa = 0f, sumh = 0f, sumv = 0f, sumsqh = 0f, sumsqv = 0f, areawt = 0f;
                            for (int i = -6; i < 7; i += 2)
                            {
                                int indx1 = indx + i * ts - 6;
                                for (int j = -6; j < 7; j += 2, indx1 += 2)
                                    if (nyquist2[indx1 >> 1] != 0)
                                    {
                                        float cfatemp = cfa[indx1];
                                        sumcfa += cfatemp;
                                        sumh += cfa[indx1 - 1] + cfa[indx1 + 1];
                                        sumv += cfa[indx1 - v1] + cfa[indx1 + v1];
                                        sumsqh += Sqr(cfatemp - cfa[indx1 - 1]) + Sqr(cfatemp - cfa[indx1 + 1]);
                                        sumsqv += Sqr(cfatemp - cfa[indx1 - v1]) + Sqr(cfatemp - cfa[indx1 + v1]);
                                        areawt += 1;
                                    }
                            }
                            sumh = sumcfa - 0.5f * sumh;
                            sumv = sumcfa - 0.5f * sumv;
                            areawt = 0.5f * areawt;
                            float hcdvar = epssq + MathF.Abs(areawt * sumsqh - sumh * sumh);
                            float vcdvar = epssq + MathF.Abs(areawt * sumsqv - sumv * sumv);
                            hvwt[indx >> 1] = hcdvar / (vcdvar + hcdvar);
                        }
                }

                // populate G at R / B sites
                for (int rr = 8; rr < rr1 - 8; rr++)
                    for (int indx = rr * ts + 8 + (Fcp(rr, 2) & 1); indx < rr * ts + cc1 - 8; indx += 2)
                    {
                        float hvwtalt = 0.25f * (hvwt[(indx - m1) >> 1] + hvwt[(indx + p1) >> 1] + hvwt[(indx - p1) >> 1] + hvwt[(indx + m1) >> 1]);
                        hvwt[indx >> 1] = MathF.Abs(0.5f - hvwt[indx >> 1]) < MathF.Abs(0.5f - hvwtalt) ? hvwtalt : hvwt[indx >> 1];
                        dgrb0[indx >> 1] = Intp(hvwt[indx >> 1], vcd[indx], hcd[indx]);
                        rgbgreen[indx] = cfa[indx] + dgrb0[indx >> 1];
                        bool ny = nyquist2[indx >> 1] != 0;
                        dgrb2h[indx >> 1] = ny ? Sqr(rgbgreen[indx] - 0.5f * (rgbgreen[indx - 1] + rgbgreen[indx + 1])) : 0f;
                        dgrb2v[indx >> 1] = ny ? Sqr(rgbgreen[indx] - 0.5f * (rgbgreen[indx - v1] + rgbgreen[indx + v1])) : 0f;
                    }

                // refine Nyquist areas using G curvatures
                if (doNyquist)
                    for (int rr = nystartrow; rr < nyendrow; rr++)
                        for (int indx = rr * ts + nystartcol + (Fcp(rr, 2) & 1); indx < rr * ts + nyendcol; indx += 2)
                        {
                            if (nyquist2[indx >> 1] == 0) continue;
                            float gvarh = epssq + (gquinc[0] * dgrb2h[indx >> 1]
                                + gquinc[1] * (dgrb2h[(indx - m1) >> 1] + dgrb2h[(indx + p1) >> 1] + dgrb2h[(indx - p1) >> 1] + dgrb2h[(indx + m1) >> 1])
                                + gquinc[2] * (dgrb2h[(indx - v2) >> 1] + dgrb2h[(indx - 2) >> 1] + dgrb2h[(indx + 2) >> 1] + dgrb2h[(indx + v2) >> 1])
                                + gquinc[3] * (dgrb2h[(indx - m2) >> 1] + dgrb2h[(indx + p2) >> 1] + dgrb2h[(indx - p2) >> 1] + dgrb2h[(indx + m2) >> 1]));
                            float gvarv = epssq + (gquinc[0] * dgrb2v[indx >> 1]
                                + gquinc[1] * (dgrb2v[(indx - m1) >> 1] + dgrb2v[(indx + p1) >> 1] + dgrb2v[(indx - p1) >> 1] + dgrb2v[(indx + m1) >> 1])
                                + gquinc[2] * (dgrb2v[(indx - v2) >> 1] + dgrb2v[(indx - 2) >> 1] + dgrb2v[(indx + 2) >> 1] + dgrb2v[(indx + v2) >> 1])
                                + gquinc[3] * (dgrb2v[(indx - m2) >> 1] + dgrb2v[(indx + p2) >> 1] + dgrb2v[(indx - p2) >> 1] + dgrb2v[(indx + m2) >> 1]));
                            dgrb0[indx >> 1] = (hcd[indx] * gvarv + vcd[indx] * gvarh) / (gvarv + gvarh);
                            rgbgreen[indx] = cfa[indx] + dgrb0[indx >> 1];
                        }

                // diagonal gradients and colour differences
                for (int rr = 6; rr < rr1 - 6; rr++)
                {
                    if ((Fcp(rr, 2) & 1) == 0)
                    {
                        for (int cc = 6, indx = rr * ts + cc; cc < cc1 - 6; cc += 2, indx += 2)
                        {
                            delp[indx >> 1] = MathF.Abs(cfa[indx + p1] - cfa[indx - p1]);
                            delm[indx >> 1] = MathF.Abs(cfa[indx + m1] - cfa[indx - m1]);
                            dgrbsq1p[indx >> 1] = Sqr(cfa[indx + 1] - cfa[indx + 1 - p1]) + Sqr(cfa[indx + 1] - cfa[indx + 1 + p1]);
                            dgrbsq1m[indx >> 1] = Sqr(cfa[indx + 1] - cfa[indx + 1 - m1]) + Sqr(cfa[indx + 1] - cfa[indx + 1 + m1]);
                        }
                    }
                    else
                    {
                        for (int cc = 6, indx = rr * ts + cc; cc < cc1 - 6; cc += 2, indx += 2)
                        {
                            dgrbsq1p[indx >> 1] = Sqr(cfa[indx] - cfa[indx - p1]) + Sqr(cfa[indx] - cfa[indx + p1]);
                            dgrbsq1m[indx >> 1] = Sqr(cfa[indx] - cfa[indx - m1]) + Sqr(cfa[indx] - cfa[indx + m1]);
                            delp[indx >> 1] = MathF.Abs(cfa[indx + 1 + p1] - cfa[indx + 1 - p1]);
                            delm[indx >> 1] = MathF.Abs(cfa[indx + 1 + m1] - cfa[indx + 1 - m1]);
                        }
                    }
                }

                // diagonal interpolation correction
                for (int rr = 8; rr < rr1 - 8; rr++)
                    for (int cc = 8 + (Fcp(rr, 2) & 1), indx = rr * ts + cc, indx1 = indx >> 1; cc < cc1 - 8; cc += 2, indx += 2, indx1++)
                    {
                        float crse = 2f * cfa[indx + m1] / (eps + cfa[indx] + cfa[indx + m2]);
                        float crnw = 2f * cfa[indx - m1] / (eps + cfa[indx] + cfa[indx - m2]);
                        float crne = 2f * cfa[indx + p1] / (eps + cfa[indx] + cfa[indx + p2]);
                        float crsw = 2f * cfa[indx - p1] / (eps + cfa[indx] + cfa[indx - p2]);

                        float rbse = MathF.Abs(1f - crse) < arthresh ? cfa[indx] * crse : cfa[indx + m1] + 0.5f * (cfa[indx] - cfa[indx + m2]);
                        float rbnw = MathF.Abs(1f - crnw) < arthresh ? cfa[indx] * crnw : cfa[indx - m1] + 0.5f * (cfa[indx] - cfa[indx - m2]);
                        float rbne = MathF.Abs(1f - crne) < arthresh ? cfa[indx] * crne : cfa[indx + p1] + 0.5f * (cfa[indx] - cfa[indx + p2]);
                        float rbsw = MathF.Abs(1f - crsw) < arthresh ? cfa[indx] * crsw : cfa[indx - p1] + 0.5f * (cfa[indx] - cfa[indx - p2]);

                        float wtse = eps + delm[indx1] + delm[(indx + m1) >> 1] + delm[(indx + m2) >> 1];
                        float wtnw = eps + delm[indx1] + delm[(indx - m1) >> 1] + delm[(indx - m2) >> 1];
                        float wtne = eps + delp[indx1] + delp[(indx + p1) >> 1] + delp[(indx + p2) >> 1];
                        float wtsw = eps + delp[indx1] + delp[(indx - p1) >> 1] + delp[(indx - p2) >> 1];

                        rbm[indx1] = (wtse * rbnw + wtnw * rbse) / (wtse + wtnw);
                        rbp[indx1] = (wtne * rbsw + wtsw * rbne) / (wtne + wtsw);

                        float rbvarm = epssq + (gausseven[0] * (dgrbsq1m[(indx - v1) >> 1] + dgrbsq1m[(indx - 1) >> 1] + dgrbsq1m[(indx + 1) >> 1] + dgrbsq1m[(indx + v1) >> 1])
                            + gausseven[1] * (dgrbsq1m[(indx - v2 - 1) >> 1] + dgrbsq1m[(indx - v2 + 1) >> 1] + dgrbsq1m[(indx - 2 - v1) >> 1] + dgrbsq1m[(indx + 2 - v1) >> 1]
                                + dgrbsq1m[(indx - 2 + v1) >> 1] + dgrbsq1m[(indx + 2 + v1) >> 1] + dgrbsq1m[(indx + v2 - 1) >> 1] + dgrbsq1m[(indx + v2 + 1) >> 1]));
                        pmwt[indx1] = rbvarm / ((epssq + (gausseven[0] * (dgrbsq1p[(indx - v1) >> 1] + dgrbsq1p[(indx - 1) >> 1] + dgrbsq1p[(indx + 1) >> 1] + dgrbsq1p[(indx + v1) >> 1])
                            + gausseven[1] * (dgrbsq1p[(indx - v2 - 1) >> 1] + dgrbsq1p[(indx - v2 + 1) >> 1] + dgrbsq1p[(indx - 2 - v1) >> 1] + dgrbsq1p[(indx + 2 - v1) >> 1]
                                + dgrbsq1p[(indx - 2 + v1) >> 1] + dgrbsq1p[(indx + 2 + v1) >> 1] + dgrbsq1p[(indx + v2 - 1) >> 1] + dgrbsq1p[(indx + v2 + 1) >> 1]))) + rbvarm);

                        // bound the interpolation in regions of high saturation
                        if (rbp[indx1] < cfa[indx])
                        {
                            if (2f * rbp[indx1] < cfa[indx])
                                rbp[indx1] = Median(rbp[indx1], cfa[indx - p1], cfa[indx + p1]);
                            else
                            {
                                float pwt = 2f * (cfa[indx] - rbp[indx1]) / (eps + rbp[indx1] + cfa[indx]);
                                rbp[indx1] = pwt * rbp[indx1] + (1f - pwt) * Median(rbp[indx1], cfa[indx - p1], cfa[indx + p1]);
                            }
                        }
                        if (rbm[indx1] < cfa[indx])
                        {
                            if (2f * rbm[indx1] < cfa[indx])
                                rbm[indx1] = Median(rbm[indx1], cfa[indx - m1], cfa[indx + m1]);
                            else
                            {
                                float mwt = 2f * (cfa[indx] - rbm[indx1]) / (eps + rbm[indx1] + cfa[indx]);
                                rbm[indx1] = mwt * rbm[indx1] + (1f - mwt) * Median(rbm[indx1], cfa[indx - m1], cfa[indx + m1]);
                            }
                        }
                        if (rbp[indx1] > clipPt) rbp[indx1] = Median(rbp[indx1], cfa[indx - p1], cfa[indx + p1]);
                        if (rbm[indx1] > clipPt) rbm[indx1] = Median(rbm[indx1], cfa[indx - m1], cfa[indx + m1]);
                    }

                for (int rr = 10; rr < rr1 - 10; rr++)
                    for (int cc = 10 + (Fcp(rr, 2) & 1), indx = rr * ts + cc, indx1 = indx >> 1; cc < cc1 - 10; cc += 2, indx += 2, indx1++)
                    {
                        float pmwtalt = 0.25f * (pmwt[(indx - m1) >> 1] + pmwt[(indx + p1) >> 1] + pmwt[(indx - p1) >> 1] + pmwt[(indx + m1) >> 1]);
                        if (MathF.Abs(0.5f - pmwt[indx1]) < MathF.Abs(0.5f - pmwtalt))
                            pmwt[indx1] = pmwtalt;
                        rbint[indx1] = 0.5f * (cfa[indx] + rbm[indx1] * (1f - pmwt[indx1]) + rbp[indx1] * pmwt[indx1]); // R + B, interpolated
                    }

                for (int rr = 12; rr < rr1 - 12; rr++)
                    for (int cc = 12 + (Fcp(rr, 2) & 1), indx = rr * ts + cc, indx1 = indx >> 1; cc < cc1 - 12; cc += 2, indx += 2, indx1++)
                    {
                        if (MathF.Abs(0.5f - pmwt[indx >> 1]) < MathF.Abs(0.5f - hvwt[indx >> 1]))
                            continue;

                        // G vertically / horizontally from R + B
                        float cru = cfa[indx - v1] * 2f / (eps + rbint[indx1] + rbint[indx1 - v1]);
                        float crd = cfa[indx + v1] * 2f / (eps + rbint[indx1] + rbint[indx1 + v1]);
                        float crl = cfa[indx - 1] * 2f / (eps + rbint[indx1] + rbint[indx1 - 1]);
                        float crr = cfa[indx + 1] * 2f / (eps + rbint[indx1] + rbint[indx1 + 1]);

                        float gu = MathF.Abs(1f - cru) < arthresh ? rbint[indx1] * cru : cfa[indx - v1] + 0.5f * (rbint[indx1] - rbint[indx1 - v1]);
                        float gd = MathF.Abs(1f - crd) < arthresh ? rbint[indx1] * crd : cfa[indx + v1] + 0.5f * (rbint[indx1] - rbint[indx1 + v1]);
                        float gl = MathF.Abs(1f - crl) < arthresh ? rbint[indx1] * crl : cfa[indx - 1] + 0.5f * (rbint[indx1] - rbint[indx1 - 1]);
                        float gr = MathF.Abs(1f - crr) < arthresh ? rbint[indx1] * crr : cfa[indx + 1] + 0.5f * (rbint[indx1] - rbint[indx1 + 1]);

                        float gintv = (dirwts0[indx - v1] * gd + dirwts0[indx + v1] * gu) / (dirwts0[indx + v1] + dirwts0[indx - v1]);
                        float ginth = (dirwts1[indx - 1] * gr + dirwts1[indx + 1] * gl) / (dirwts1[indx - 1] + dirwts1[indx + 1]);

                        if (gintv < rbint[indx1])
                        {
                            if (2 * gintv < rbint[indx1])
                                gintv = Median(gintv, cfa[indx - v1], cfa[indx + v1]);
                            else
                            {
                                float vwt = 2f * (rbint[indx1] - gintv) / (eps + gintv + rbint[indx1]);
                                gintv = vwt * gintv + (1f - vwt) * Median(gintv, cfa[indx - v1], cfa[indx + v1]);
                            }
                        }
                        if (ginth < rbint[indx1])
                        {
                            if (2 * ginth < rbint[indx1])
                                ginth = Median(ginth, cfa[indx - 1], cfa[indx + 1]);
                            else
                            {
                                float hwt = 2f * (rbint[indx1] - ginth) / (eps + ginth + rbint[indx1]);
                                ginth = hwt * ginth + (1f - hwt) * Median(ginth, cfa[indx - 1], cfa[indx + 1]);
                            }
                        }
                        if (ginth > clipPt) ginth = Median(ginth, cfa[indx - 1], cfa[indx + 1]);
                        if (gintv > clipPt) gintv = Median(gintv, cfa[indx - v1], cfa[indx + v1]);

                        rgbgreen[indx] = ginth * (1f - hvwt[indx1]) + gintv * hvwt[indx1];
                        dgrb0[indx >> 1] = rgbgreen[indx] - cfa[indx];
                    }

                // chrominance: split G - B from G - R ((ey, ex) is the red site)
                for (int rr = 13 - ey; rr < rr1 - 12; rr += 2)
                    for (int indx1 = (rr * ts + 13 - ex) >> 1; indx1 < (rr * ts + cc1 - 12) >> 1; indx1++)
                    {
                        dgrb1[indx1] = dgrb0[indx1];
                        dgrb0[indx1] = 0;
                    }

                for (int rr = 14; rr < rr1 - 14; rr++)
                    for (int cc = 14 + (Fcp(rr, 2) & 1), indx = rr * ts + cc, c = 1 - Fcp(rr, cc) / 2; cc < cc1 - 14; cc += 2, indx += 2)
                    {
                        var d = c == 0 ? dgrb0 : dgrb1;
                        float wtnw = 1f / (eps + MathF.Abs(d[(indx - m1) >> 1] - d[(indx + m1) >> 1]) + MathF.Abs(d[(indx - m1) >> 1] - d[(indx - m3) >> 1]) + MathF.Abs(d[(indx + m1) >> 1] - d[(indx - m3) >> 1]));
                        float wtne = 1f / (eps + MathF.Abs(d[(indx + p1) >> 1] - d[(indx - p1) >> 1]) + MathF.Abs(d[(indx + p1) >> 1] - d[(indx + p3) >> 1]) + MathF.Abs(d[(indx - p1) >> 1] - d[(indx + p3) >> 1]));
                        float wtsw = 1f / (eps + MathF.Abs(d[(indx - p1) >> 1] - d[(indx + p1) >> 1]) + MathF.Abs(d[(indx - p1) >> 1] - d[(indx + m3) >> 1]) + MathF.Abs(d[(indx + p1) >> 1] - d[(indx - p3) >> 1]));
                        float wtse = 1f / (eps + MathF.Abs(d[(indx + m1) >> 1] - d[(indx - m1) >> 1]) + MathF.Abs(d[(indx + m1) >> 1] - d[(indx - p3) >> 1]) + MathF.Abs(d[(indx - m1) >> 1] - d[(indx + m3) >> 1]));
                        d[indx >> 1] = (wtnw * (1.325f * d[(indx - m1) >> 1] - 0.175f * d[(indx - m3) >> 1] - 0.075f * d[(indx - m1 - 2) >> 1] - 0.075f * d[(indx - m1 - v2) >> 1])
                            + wtne * (1.325f * d[(indx + p1) >> 1] - 0.175f * d[(indx + p3) >> 1] - 0.075f * d[(indx + p1 + 2) >> 1] - 0.075f * d[(indx + p1 + v2) >> 1])
                            + wtsw * (1.325f * d[(indx - p1) >> 1] - 0.175f * d[(indx - p3) >> 1] - 0.075f * d[(indx - p1 - 2) >> 1] - 0.075f * d[(indx - p1 - v2) >> 1])
                            + wtse * (1.325f * d[(indx + m1) >> 1] - 0.175f * d[(indx + m3) >> 1] - 0.075f * d[(indx + m1 + 2) >> 1] - 0.075f * d[(indx + m1 + v2) >> 1]))
                            / (wtnw + wtne + wtsw + wtse);
                    }

                // red and blue: at R / B sites from the colour differences, at G sites interpolated
                float GreenSite(float[] dg, int indx) =>
                    MathF.Max(0f, 65535f * (rgbgreen[indx] - (hvwt[(indx - v1) >> 1] * dg[(indx - v1) >> 1] + (1f - hvwt[(indx + 1) >> 1]) * dg[(indx + 1) >> 1]
                        + (1f - hvwt[(indx - 1) >> 1]) * dg[(indx - 1) >> 1] + hvwt[(indx + v1) >> 1] * dg[(indx + v1) >> 1])
                        / (hvwt[(indx - v1) >> 1] + 2f - hvwt[(indx + 1) >> 1] - hvwt[(indx - 1) >> 1] + hvwt[(indx + v1) >> 1])));
                for (int rr = 16; rr < rr1 - 16; rr++)
                {
                    int row = rr + top;
                    int col = left + 16;
                    int indx = rr * ts + 16;
                    int rowBase = row * width;
                    if ((Fcp(rr, 2) & 1) == 1)
                    {
                        for (; indx < rr * ts + cc1 - 16 - (cc1 & 1); indx++, col++)
                        {
                            red[rowBase + col] = GreenSite(dgrb0, indx);
                            blue[rowBase + col] = GreenSite(dgrb1, indx);
                            indx++;
                            col++;
                            red[rowBase + col] = MathF.Max(0f, 65535f * (rgbgreen[indx] - dgrb0[indx >> 1]));
                            blue[rowBase + col] = MathF.Max(0f, 65535f * (rgbgreen[indx] - dgrb1[indx >> 1]));
                        }
                        if ((cc1 & 1) != 0)
                        {
                            red[rowBase + col] = GreenSite(dgrb0, indx);
                            blue[rowBase + col] = GreenSite(dgrb1, indx);
                        }
                    }
                    else
                    {
                        for (; indx < rr * ts + cc1 - 16 - (cc1 & 1); indx++, col++)
                        {
                            red[rowBase + col] = MathF.Max(0f, 65535f * (rgbgreen[indx] - dgrb0[indx >> 1]));
                            blue[rowBase + col] = MathF.Max(0f, 65535f * (rgbgreen[indx] - dgrb1[indx >> 1]));
                            indx++;
                            col++;
                            red[rowBase + col] = GreenSite(dgrb0, indx);
                            blue[rowBase + col] = GreenSite(dgrb1, indx);
                        }
                        if ((cc1 & 1) != 0)
                        {
                            red[rowBase + col] = MathF.Max(0f, 65535f * (rgbgreen[indx] - dgrb0[indx >> 1]));
                            blue[rowBase + col] = MathF.Max(0f, 65535f * (rgbgreen[indx] - dgrb1[indx >> 1]));
                        }
                    }
                }

                for (int rr = 16; rr < rr1 - 16; rr++)
                {
                    int row = rr + top;
                    for (int cc = 16; cc < cc1 - 16; cc++)
                        green[row * width + cc + left] = MathF.Max(0f, 65535f * rgbgreen[rr * ts + cc]);
                }
                return b;
            },
            _ => { });
    }

    private sealed class AmazeBuffers(int ts)
    {
        private readonly int _full = ts * ts, _half = ts * ts / 2;
        public readonly float[] Cfa = new float[ts * ts], RgbGreen = new float[ts * ts], DelHvSqSum = new float[ts * ts];
        public readonly float[] DirWts0 = new float[ts * ts], DirWts1 = new float[ts * ts];
        public readonly float[] Vcd = new float[ts * ts], Hcd = new float[ts * ts], VcdAlt = new float[ts * ts], HcdAlt = new float[ts * ts];
        public readonly float[] CdDiffSq = new float[ts * ts], DgIntV = new float[ts * ts], DgIntH = new float[ts * ts];
        public readonly float[] HvWt = new float[ts * ts / 2], Dgrb0 = new float[ts * ts / 2], Dgrb1 = new float[ts * ts / 2];
        public readonly float[] DelP = new float[ts * ts / 2], DelM = new float[ts * ts / 2], RbInt = new float[ts * ts / 2];
        public readonly float[] Dgrb2H = new float[ts * ts / 2], Dgrb2V = new float[ts * ts / 2];
        public readonly float[] DgrbSq1M = new float[ts * ts / 2], DgrbSq1P = new float[ts * ts / 2], PmWt = new float[ts * ts / 2];
        public readonly float[] Rbm = new float[ts * ts / 2], Rbp = new float[ts * ts / 2], NyquTest = new float[ts * ts / 2];
        public readonly byte[] Nyquist = new byte[ts * ts / 2], Nyquist2 = new byte[ts * ts / 2];

        public void Clear()
        {
            foreach (var a in new[] { Cfa, RgbGreen, DelHvSqSum, DirWts0, DirWts1, Vcd, Hcd, VcdAlt, HcdAlt, CdDiffSq, DgIntV, DgIntH })
                Array.Clear(a, 0, _full);
            foreach (var a in new[] { HvWt, Dgrb0, Dgrb1, DelP, DelM, RbInt, Dgrb2H, Dgrb2V, DgrbSq1M, DgrbSq1P, PmWt, Rbm, Rbp, NyquTest })
                Array.Clear(a, 0, _half);
            Array.Clear(Nyquist);
            Array.Clear(Nyquist2);
        }
    }
}
