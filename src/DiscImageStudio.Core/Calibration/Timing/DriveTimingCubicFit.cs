namespace DiscImageStudio.Core.Calibration;

/// <summary>The cubic track-pitch shape recovered from one timing run. The constant-pitch
/// result is only a first guess; fitting the shape is what removes the residual bend without
/// burning a second disc. Adapted from POPCORNBOOM/Discratcher (GPL-3.0-or-later),
/// crates/disc-core/src/calib/solver.rs and ladder.rs.</summary>
/// <summary>One unwrapped phase measurement in channel-bit coordinates.</summary>
public sealed record DriveTimingCubicPoint(double Bits, double ThetaDegrees, double Sigma);

public sealed record DriveTimingCubicResult(
    double InnerRadiusMm, double OuterRadiusMm,
    double PitchLinear, double PitchQuadratic, double PitchCubic,
    double BasePitchUm, double RmsDegrees, int TurnRepairs,
    CalibrationParameters Parameters);

public static class DriveTimingCubicFit
{
    /// <summary>Weight on the shape terms, in deg^2 per (1e-4)^2. Weak: it only stops the
    /// edges beyond the measured span from swinging.</summary>
    public const double Lambda = 1e-3;
    private const double MisfitFactor = 2.5;
    private const int Grid = 8001;

    /// <summary>Fit a timing record. The record carries the constant-pitch starting point;
    /// the cubic shape is recovered from its unwrapped phase targets.</summary>
    public static DriveTimingCubicResult? TryFit(DriveTimingResult result, long totalSectors)
    {
        ArgumentNullException.ThrowIfNull(result);
        try { result.Validate(); }
        catch (ArgumentException) { return null; }
        if (totalSectors <= 0 || totalSectors <= result.Targets.Max(t => t.Lba)) return null;

        double bitMm = result.AssumedBitLengthMm;
        double totalBits = totalSectors * (result.Media == DriveTimingMedia.Dvd ? 38_688 : 57_624);
        double nRef = DriveTimingGeometry.ReadEndBits(result.Media, result.ReferenceLba);
        double ri0 = result.U * bitMm;
        double ro0 = bitMm * Math.Sqrt(result.U * result.U + result.V * totalBits / Math.PI);
        var points = result.Targets.OrderBy(t => t.ChannelBits)
            .Select(t => new DriveTimingCubicPoint(t.ChannelBits, t.UnwrappedDegrees, t.SigmaDegrees)).ToArray();
        return TryFit(points, nRef, ri0, ro0, result.Media, totalSectors);
    }

    /// <summary>Fit an externally supplied unwrapped phase table. This is also the entry
    /// point for validating against another implementation's recorded rungs.</summary>
    public static DriveTimingCubicResult? TryFit(IReadOnlyList<DriveTimingCubicPoint> source,
        double nRef, double ri0, double ro0, DriveTimingMedia media, long totalSectors)
    {
        ArgumentNullException.ThrowIfNull(source);
        if (source.Count < 8 || totalSectors <= 0) return null;
        double bitMm = media == DriveTimingMedia.Dvd ? 133.3e-6 : 1200.0 / 4_321_800;
        double totalBits = totalSectors * (media == DriveTimingMedia.Dvd ? 38_688 : 57_624);
        if (!double.IsFinite(ri0) || !double.IsFinite(ro0) || ri0 <= 0 || ro0 <= ri0) return null;

        DriveTimingCubicPoint[] points = source.OrderBy(p => p.Bits).ToArray();
        double noise = Math.Max(points.Average(p => p.Sigma), .25);
        double bar = MisfitFactor * noise;

        Fit? cubic = FitAt(points, nRef, ri0, ro0, bitMm, totalBits);
        if (cubic is null) return null;
        if (cubic.RmsDegrees > bar && ScanRi(points, nRef, ri0, ro0, bitMm, totalBits) is { } scanned
            && scanned.RmsDegrees < cubic.RmsDegrees)
        {
            cubic = scanned;
        }

        int repairs = 0;
        for (int pass = 0; pass < 8 && cubic.RmsDegrees > bar; pass++)
        {
            int[] worst = Enumerable.Range(0, points.Length)
                .OrderByDescending(i => Math.Abs(cubic.Residuals[i])).Take(6).ToArray();
            Fit? best = null;
            int bestIndex = -1;
            double bestTurn = 0;
            foreach (int i in worst)
            {
                foreach (double turn in new[] { 360.0, -360.0 })
                {
                    points[i] = points[i] with { ThetaDegrees = points[i].ThetaDegrees + turn };
                    Fit? trial = ScanRi(points, nRef, ri0, ro0, bitMm, totalBits);
                    points[i] = points[i] with { ThetaDegrees = points[i].ThetaDegrees - turn };
                    if (trial is not null && trial.RmsDegrees < .7 * cubic.RmsDegrees
                        && (best is null || trial.RmsDegrees < best.RmsDegrees))
                    {
                        best = trial; bestIndex = i; bestTurn = turn;
                    }
                }
            }

            if (best is null || bestIndex < 0) break;
            points[bestIndex] = points[bestIndex] with { ThetaDegrees = points[bestIndex].ThetaDegrees + bestTurn };
            cubic = best;
            repairs++;
        }

        Fit final = cubic;
        if (!double.IsFinite(final.RmsDegrees) || final.RmsDegrees > 30
            || !AllFinite(final.Ro, final.C1, final.C2, final.C3, final.BasePitchUm)) return null;

        var parameters = new CalibrationParameters(
            media == DriveTimingMedia.Dvd ? CalibrationDiscKind.Dvd : CalibrationDiscKind.Cd,
            final.Ri, final.Ro, totalSectors,
            media == DriveTimingMedia.Dvd ? 133.3 : 1200.0,
            final.C1, final.C2, final.C3);
        return new(final.Ri, final.Ro, final.C1, final.C2, final.C3,
            final.BasePitchUm, final.RmsDegrees, repairs, parameters);
    }

    private static bool AllFinite(params double[] values) => values.All(double.IsFinite);


    private sealed class Fit
    {
        public required double Ri { get; init; }
        public required double Ro { get; init; }
        public required double C1 { get; init; }
        public required double C2 { get; init; }
        public required double C3 { get; init; }
        public required double BasePitchUm { get; init; }
        public required double RmsDegrees { get; init; }
        public required double[] Residuals { get; init; }
    }

    /// <summary>Holds ri and fits (ro, c1, c2, c3) plus the phase offset directly on the
    /// measured unwrapped angles.</summary>
    private static Fit? FitAt(DriveTimingCubicPoint[] points, double nRef, double ri, double ro,
        double bitMm, double totalBits)
    {
        double[] x = [ro, 0, 0, 0, 0];
        double[] steps = [1e-7, 1e-7, 1e-7, 1e-7, 1e-2];
        double reg = Math.Sqrt(Lambda) / 1e-4;

        double[] Residual(double[] p)
        {
            Spiral? spiral = Spiral.TryCreate(ri, p[0], p[1], p[2], p[3], totalBits, bitMm);
            if (spiral is null) return Enumerable.Repeat(1e6, points.Length + 3).ToArray();
            double reference = spiral.ThetaAtBit(nRef);
            double[] result = new double[points.Length + 3];
            for (int i = 0; i < points.Length; i++)
                result[i] = (spiral.ThetaAtBit(points[i].Bits) - reference) * 180 / Math.PI + p[4] - points[i].ThetaDegrees;
            for (int k = 1; k <= 3; k++) result[points.Length + k - 1] = p[k] * reg;
            return result;
        }

        double[,] Jacobian(double[] p, double[] current)
        {
            double[,] j = new double[current.Length, 5];
            for (int k = 0; k < 5; k++)
            {
                double[] plus = (double[])p.Clone(), minus = (double[])p.Clone();
                plus[k] += steps[k]; minus[k] -= steps[k];
                double[] a = Residual(plus), b = Residual(minus);
                for (int i = 0; i < current.Length; i++) j[i, k] = (a[i] - b[i]) / (2 * steps[k]);
            }
            return j;
        }

        double[] r = Residual(x);
        double cost = r.Sum(v => v * v);
        double mu = 1e-8;
        for (int iteration = 0; iteration < 200; iteration++)
        {
            double[,] j = Jacobian(x, r);
            double[] scale = new double[5];
            for (int k = 0; k < 5; k++)
            {
                double sum = 0;
                for (int i = 0; i < r.Length; i++) sum += j[i, k] * j[i, k];
                scale[k] = Math.Max(Math.Sqrt(sum), 1e-300);
            }
            bool improved = false;
            while (mu < 1e12)
            {
                double[,] a = new double[5, 5];
                double[] b = new double[5];
                for (int i = 0; i < r.Length; i++)
                {
                    double[] row = new double[5];
                    for (int k = 0; k < 5; k++) row[k] = j[i, k] / scale[k];
                    for (int p = 0; p < 5; p++)
                    {
                        b[p] -= row[p] * r[i];
                        for (int q = 0; q < 5; q++) a[p, q] += row[p] * row[q];
                    }
                }
                for (int k = 0; k < 5; k++) a[k, k] += mu;
                double[]? delta = Solve(a, b);
                if (delta is not null)
                {
                    double[] trial = new double[5];
                    for (int k = 0; k < 5; k++) trial[k] = x[k] + delta[k] / scale[k];
                    double[] tr = Residual(trial);
                    double tc = tr.Sum(v => v * v);
                    if (double.IsFinite(tc) && tc < cost)
                    {
                        bool small = cost - tc <= 1e-12 * cost;
                        x = trial; r = tr; cost = tc;
                        mu = Math.Max(mu * .2, 1e-14);
                        improved = !small;
                        break;
                    }
                }
                mu *= 8;
            }
            if (!improved) break;
        }

        Spiral? finalSpiral = Spiral.TryCreate(ri, x[0], x[1], x[2], x[3], totalBits, bitMm);
        if (finalSpiral is null) return null;
        double finalReference = finalSpiral.ThetaAtBit(nRef);
        double[] raw = points.Select(p => (finalSpiral.ThetaAtBit(p.Bits) - finalReference) * 180 / Math.PI - p.ThetaDegrees).ToArray();
        double offset = raw.Average();
        double[] residuals = raw.Select(v => v - offset).ToArray();
        double rms = Math.Sqrt(residuals.Sum(v => v * v) / raw.Length);
        return new Fit
        {
            Ri = ri, Ro = x[0], C1 = x[1], C2 = x[2], C3 = x[3],
            BasePitchUm = finalSpiral.PitchMm * 1000, RmsDegrees = rms, Residuals = residuals,
        };
    }

    /// <summary>ri and c1 are almost degenerate. A one-dimensional scan around the
    /// well-behaved four-parameter fit keeps the track area roughly fixed while ri moves.</summary>
    private static Fit? ScanRi(DriveTimingCubicPoint[] points, double nRef, double ri0, double ro0,
        double bitMm, double totalBits)
    {
        Fit? At(double ri)
        {
            double ro = Math.Sqrt(ro0 * ro0 + ri * ri - ri0 * ri0);
            return ro > ri + 1e-6 ? FitAt(points, nRef, ri, ro, bitMm, totalBits) : null;
        }
        const double span = .36;
        const int steps = 9;
        double step = span / steps;
        Fit? best = At(ri0);
        if (best is null) return null;
        double bestRi = ri0;
        for (int k = -steps; k <= steps; k++)
        {
            if (k == 0) continue;
            double ri = ri0 + k * step;
            if (At(ri) is { } candidate && candidate.RmsDegrees < best.RmsDegrees)
            {
                best = candidate; bestRi = ri;
            }
        }
        double lo = bestRi - step, hi = bestRi + step;
        for (int iteration = 0; iteration < 24; iteration++)
        {
            double a = lo + (hi - lo) * .382, b = lo + (hi - lo) * .618;
            Fit? fa = At(a), fb = At(b);
            if (fa is null || fb is null) break;
            if (fa.RmsDegrees < fb.RmsDegrees)
            {
                hi = b;
                if (fa.RmsDegrees < best.RmsDegrees) { best = fa; bestRi = a; }
            }
            else
            {
                lo = a;
                if (fb.RmsDegrees < best.RmsDegrees) { best = fb; bestRi = b; }
            }
        }
        return best;
    }

    private static double[]? Solve(double[,] a, double[] b)
    {
        const int n = 5;
        double[,] m = new double[n, n + 1];
        for (int i = 0; i < n; i++)
        {
            for (int j = 0; j < n; j++) m[i, j] = a[i, j];
            m[i, n] = b[i];
        }
        for (int col = 0; col < n; col++)
        {
            int pivot = col;
            for (int row = col + 1; row < n; row++)
                if (Math.Abs(m[row, col]) > Math.Abs(m[pivot, col])) pivot = row;
            if (Math.Abs(m[pivot, col]) < 1e-300) return null;
            if (pivot != col)
                for (int j = col; j <= n; j++) (m[col, j], m[pivot, j]) = (m[pivot, j], m[col, j]);
            for (int row = col + 1; row < n; row++)
            {
                double factor = m[row, col] / m[col, col];
                if (factor == 0) continue;
                for (int j = col; j <= n; j++) m[row, j] -= factor * m[col, j];
            }
        }
        double[] result = new double[n];
        for (int row = n - 1; row >= 0; row--)
        {
            double sum = m[row, n];
            for (int j = row + 1; j < n; j++) sum -= m[row, j] * result[j];
            result[row] = sum / m[row, row];
        }
        return result.All(double.IsFinite) ? result : null;
    }

    /// <summary>p(x) = p0 * (1 + c1*x + c2*x^2 + c3*x^3), integrated on a dense radius grid.
    /// p0 follows from requiring the track to be exactly the recorded capacity long.</summary>
    private sealed class Spiral
    {
        private readonly double[] _bits;
        private readonly double[] _theta;
        public double PitchMm { get; }

        private Spiral(double[] bits, double[] theta, double pitchMm)
        {
            _bits = bits; _theta = theta; PitchMm = pitchMm;
        }

        public static Spiral? TryCreate(double ri, double ro, double c1, double c2, double c3,
            double totalBits, double bitMm)
        {
            if (!AllFinite(ri, ro, c1, c2, c3) || ro <= ri || ri <= 0 || totalBits <= 0 || bitMm <= 0) return null;
            double dr = (ro - ri) / (Grid - 1);
            double[] n = new double[Grid], theta = new double[Grid];
            double sUnit = 0, tUnit = 0, previousS = 0, previousT = 0;
            for (int k = 0; k < Grid; k++)
            {
                double r = ri + k * dr, x = (double)k / (Grid - 1);
                double shape = 1 + x * (c1 + x * (c2 + x * c3));
                if (shape <= 0 || !double.IsFinite(shape)) return null;
                double currentS = 2 * Math.PI * r / shape, currentT = 2 * Math.PI / shape;
                if (k > 0)
                {
                    sUnit += .5 * (currentS + previousS) * dr;
                    tUnit += .5 * (currentT + previousT) * dr;
                }
                previousS = currentS; previousT = currentT;
                n[k] = sUnit; theta[k] = tUnit;
            }
            double p0 = sUnit / (totalBits * bitMm);
            if (!double.IsFinite(p0) || p0 <= 0) return null;
            for (int k = 0; k < Grid; k++) { n[k] /= p0 * bitMm; theta[k] /= p0; }
            return new(n, theta, p0);
        }

        public double ThetaAtBit(double bits)
        {
            int low = 0, high = _bits.Length - 1;
            if (bits <= _bits[0]) return _theta[0];
            if (bits >= _bits[high]) return _theta[high];
            while (high - low > 1)
            {
                int mid = (low + high) / 2;
                if (_bits[mid] <= bits) low = mid; else high = mid;
            }
            double span = _bits[high] - _bits[low];
            double t = span > 0 ? (bits - _bits[low]) / span : 0;
            return _theta[low] + t * (_theta[high] - _theta[low]);
        }
    }
}
