using System.Collections.Generic;
using DesktopBoxesUI.Core.Interfaces;
using DesktopBoxesUI.Core.Models;

namespace DesktopBoxesUI.Core.Services;

/// <summary>
/// Pure-geometry implementation of <see cref="IWindowSnappingService"/>. Snaps a Box window's
/// edges/center to target edges/centers. Padding is applied only to "touching" pairs (this Left to
/// other Right, this Top to other Bottom, etc.) so the moving Box keeps a gap from the other Box;
/// same-side alignment (Top to Top, Left to Left) and centers stay flush. Screen targets are never
/// padded. All inputs/outputs share one coordinate space (physical device pixels).
/// </summary>
public sealed class WindowSnappingService : IWindowSnappingService
{
    public SnapResult SnapMove(RectD moving, IReadOnlyList<RectD> screenTargets, IReadOnlyList<RectD> boxTargets, double threshold, double padding)
    {
        double bestDX = double.PositiveInfinity;
        double bestDY = double.PositiveInfinity;
        GuideLine gv = default;
        GuideLine gh = default;
        bool hasV = false;
        bool hasH = false;

        var movingXs = new[] { moving.X, moving.Right, (moving.X + moving.Right) / 2d };
        var movingYs = new[] { moving.Y, moving.Bottom, (moving.Y + moving.Bottom) / 2d };

        foreach (var t in screenTargets)
        {
            SnapTranslateVertical(movingXs, t, moving, threshold, 0, ref bestDX, ref gv, ref hasV);
            SnapTranslateHorizontal(movingYs, t, moving, threshold, 0, ref bestDY, ref gh, ref hasH);
        }

        foreach (var t in boxTargets)
        {
            SnapTranslateVertical(movingXs, t, moving, threshold, padding, ref bestDX, ref gv, ref hasV);
            SnapTranslateHorizontal(movingYs, t, moving, threshold, padding, ref bestDY, ref gh, ref hasH);
        }

        double dx = double.IsPositiveInfinity(bestDX) ? 0 : bestDX;
        double dy = double.IsPositiveInfinity(bestDY) ? 0 : bestDY;
        var rect = RectD.FromXYWH(moving.X + dx, moving.Y + dy, moving.Width, moving.Height);

        var guides = new List<GuideLine>();
        if (hasV) guides.Add(gv);
        if (hasH) guides.Add(gh);
        return new SnapResult(rect, guides);
    }

    public SnapResult SnapResize(RectD moving, ResizeEdge edge, SizeD minSize, IReadOnlyList<RectD> screenTargets, IReadOnlyList<RectD> boxTargets, double threshold, double padding)
    {
        bool mL = edge is ResizeEdge.Left or ResizeEdge.TopLeft or ResizeEdge.BottomLeft;
        bool mR = edge is ResizeEdge.Right or ResizeEdge.TopRight or ResizeEdge.BottomRight;
        bool mT = edge is ResizeEdge.Top or ResizeEdge.TopLeft or ResizeEdge.TopRight;
        bool mB = edge is ResizeEdge.Bottom or ResizeEdge.BottomLeft or ResizeEdge.BottomRight;

        double bestLX = double.PositiveInfinity, bestRX = double.PositiveInfinity;
        double bestTY = double.PositiveInfinity, bestBY = double.PositiveInfinity;
        GuideLine gL = default, gR = default, gT = default, gB = default;
        bool hL = false, hR = false, hT = false, hB = false;

        foreach (var t in screenTargets)
        {
            if (mL) SnapEdgeVertical(moving.X, 0, t, moving, threshold, 0, ref bestLX, ref gL, ref hL);
            if (mR) SnapEdgeVertical(moving.Right, 1, t, moving, threshold, 0, ref bestRX, ref gR, ref hR);
            if (mT) SnapEdgeHorizontal(moving.Y, 0, t, moving, threshold, 0, ref bestTY, ref gT, ref hT);
            if (mB) SnapEdgeHorizontal(moving.Bottom, 1, t, moving, threshold, 0, ref bestBY, ref gB, ref hB);
        }

        foreach (var t in boxTargets)
        {
            if (mL) SnapEdgeVertical(moving.X, 0, t, moving, threshold, padding, ref bestLX, ref gL, ref hL);
            if (mR) SnapEdgeVertical(moving.Right, 1, t, moving, threshold, padding, ref bestRX, ref gR, ref hR);
            if (mT) SnapEdgeHorizontal(moving.Y, 0, t, moving, threshold, padding, ref bestTY, ref gT, ref hT);
            if (mB) SnapEdgeHorizontal(moving.Bottom, 1, t, moving, threshold, padding, ref bestBY, ref gB, ref hB);
        }

        double L = moving.X + (mL ? (double.IsPositiveInfinity(bestLX) ? 0 : bestLX) : 0);
        double R = moving.Right + (mR ? (double.IsPositiveInfinity(bestRX) ? 0 : bestRX) : 0);
        double T = moving.Y + (mT ? (double.IsPositiveInfinity(bestTY) ? 0 : bestTY) : 0);
        double B = moving.Bottom + (mB ? (double.IsPositiveInfinity(bestBY) ? 0 : bestBY) : 0);

        double minW = minSize.Width;
        double minH = minSize.Height;

        if (mL && !mR && R - L < minW) L = R - minW;
        if (mR && !mL && R - L < minW) R = L + minW;
        if (mT && !mB && B - T < minH) T = B - minH;
        if (mB && !mT && B - T < minH) B = T + minH;
        if (mL && mR && R - L < minW) R = L + minW;
        if (mT && mB && B - T < minH) B = T + minH;

        var rect = RectD.FromXYWH(L, T, R - L, B - T);
        var guides = new List<GuideLine>();
        if (hL) guides.Add(gL);
        if (hR) guides.Add(gR);
        if (hT) guides.Add(gT);
        if (hB) guides.Add(gB);
        return new SnapResult(rect, guides);
    }

    // Edge index: 0 = leading (Left/Top), 1 = trailing (Right/Bottom), 2 = center.

    private static void SnapTranslateVertical(double[] movingXs, RectD t, RectD moving, double threshold, double padding, ref double bestDX, ref GuideLine gv, ref bool hasV)
    {
        var realXs = new[] { t.X, t.Right, (t.X + t.Right) / 2d };

        for (int i = 0; i < movingXs.Length; i++)
        {
            for (int j = 0; j < realXs.Length; j++)
            {
                double sx = realXs[j] + PadOffsetX(i, j, padding);
                double d = sx - movingXs[i];
                if (Math.Abs(d) <= threshold && Math.Abs(d) < Math.Abs(bestDX))
                {
                    bestDX = d;
                    gv = new GuideLine(true, realXs[j], Math.Min(moving.Y, t.Y), Math.Max(moving.Bottom, t.Bottom));
                    hasV = true;
                }
            }
        }
    }

    private static void SnapTranslateHorizontal(double[] movingYs, RectD t, RectD moving, double threshold, double padding, ref double bestDY, ref GuideLine gh, ref bool hasH)
    {
        var realYs = new[] { t.Y, t.Bottom, (t.Y + t.Bottom) / 2d };

        for (int i = 0; i < movingYs.Length; i++)
        {
            for (int j = 0; j < realYs.Length; j++)
            {
                double sy = realYs[j] + PadOffsetY(i, j, padding);
                double d = sy - movingYs[i];
                if (Math.Abs(d) <= threshold && Math.Abs(d) < Math.Abs(bestDY))
                {
                    bestDY = d;
                    gh = new GuideLine(false, realYs[j], Math.Min(moving.X, t.X), Math.Max(moving.Right, t.Right));
                    hasH = true;
                }
            }
        }
    }

    private static void SnapEdgeVertical(double movingX, int movingIdx, RectD t, RectD moving, double threshold, double padding, ref double best, ref GuideLine g, ref bool has)
    {
        var realXs = new[] { t.X, t.Right, (t.X + t.Right) / 2d };

        for (int j = 0; j < realXs.Length; j++)
        {
            double sx = realXs[j] + PadOffsetX(movingIdx, j, padding);
            double d = sx - movingX;
            if (Math.Abs(d) <= threshold && Math.Abs(d) < Math.Abs(best))
            {
                best = d;
                g = new GuideLine(true, realXs[j], Math.Min(moving.Y, t.Y), Math.Max(moving.Bottom, t.Bottom));
                has = true;
            }
        }
    }

    private static void SnapEdgeHorizontal(double movingY, int movingIdx, RectD t, RectD moving, double threshold, double padding, ref double best, ref GuideLine g, ref bool has)
    {
        var realYs = new[] { t.Y, t.Bottom, (t.Y + t.Bottom) / 2d };

        for (int j = 0; j < realYs.Length; j++)
        {
            double sy = realYs[j] + PadOffsetY(movingIdx, j, padding);
            double d = sy - movingY;
            if (Math.Abs(d) <= threshold && Math.Abs(d) < Math.Abs(best))
            {
                best = d;
                g = new GuideLine(false, realYs[j], Math.Min(moving.X, t.X), Math.Max(moving.Right, t.Right));
                has = true;
            }
        }
    }

    /// <summary>Pad applied to a target X edge when a touching pair is snapped (gap between Boxes).</summary>
    private static double PadOffsetX(int movingIdx, int targetIdx, double pad)
    {
        if (movingIdx == 0 && targetIdx == 1) return pad;  // this Left to other Right -> this sits to the right, gap outside
        if (movingIdx == 1 && targetIdx == 0) return -pad; // this Right to other Left -> this sits to the left, gap outside
        return 0;
    }

    /// <summary>Pad applied to a target Y edge when a touching pair is snapped (gap between Boxes).</summary>
    private static double PadOffsetY(int movingIdx, int targetIdx, double pad)
    {
        if (movingIdx == 0 && targetIdx == 1) return pad;  // this Top to other Bottom -> this sits below, gap outside
        if (movingIdx == 1 && targetIdx == 0) return -pad; // this Bottom to other Top -> this sits above, gap outside
        return 0;
    }
}
