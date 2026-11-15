using DiscImageStudio.Core.Calibration;

/// <summary>The cubic pitch fit is the part that came from Discratcher rather than the
/// constant-pitch timing probe. These checks generate phase rungs from a known spiral with
/// an independent integrator and verify the fitter recovers its shape coefficients, then
/// verify the record-based entry point behaves on a constant-pitch (zero-shape) disc.</summary>
internal static class DriveTimingCubicFitTests
{
    private const long Sectors = 2_295_104;
    private const double BitMm = 133.3e-6;
    private const double SectorBits = 38_688;

    internal static void Run()
    {
        const double ri = 24.15, ro = 57.9, c1 = -1.2e-4, c2 = 3.0e-4, c3 = -2.5e-4;
        double totalBits = Sectors * SectorBits;
        var truth = new CubicTruth(ri, ro, c1, c2, c3, totalBits, BitMm);
        uint[] lbas = Enumerable.Range(0, 56)
            .Select(i => (uint)((long)(Sectors * (.03 + .84 * i / 55.0)) / 16 * 16))
            .Distinct().ToArray();
        double[] bits = lbas.Select(l => DriveTimingGeometry.ReadEndBits(DriveTimingMedia.Dvd, l)).ToArray();
        double reference = bits[0];
        var points = bits.Select((n, i) => new DriveTimingCubicPoint(n,
            (truth.Theta(n) - truth.Theta(reference)) * 180 / Math.PI + .01 * Math.Sin(i * 1.7), .2)).ToArray();

        // The starting radii are deliberately off: only the ri scan can recover the geometry.
        DriveTimingCubicResult? fit = DriveTimingCubicFit.TryFit(points, reference, 24.0, 58.0,
            DriveTimingMedia.Dvd, Sectors);
        Require(fit is not null, "a cubic result is produced from a phase table");
        Require(fit!.RmsDegrees < .5, $"fitted rms stays small ({fit.RmsDegrees:F3} deg)");
        Require(Math.Abs(fit.PitchLinear - c1) < 8e-5 && Math.Abs(fit.PitchQuadratic - c2) < 8e-5
            && Math.Abs(fit.PitchCubic - c3) < 8e-5,
            $"the three shape coefficients are recovered ({fit.PitchLinear:E3} {fit.PitchQuadratic:E3} {fit.PitchCubic:E3})");
        Require(Math.Abs(fit.InnerRadiusMm - ri) < .08 && Math.Abs(fit.OuterRadiusMm - ro) < .08,
            $"the radii are recovered ({fit.InnerRadiusMm:F4}, {fit.OuterRadiusMm:F4} mm)");
        Require(fit.Parameters.Kind == CalibrationDiscKind.Dvd && fit.Parameters.Sectors == Sectors
            && fit.Parameters.PitchLinear == fit.PitchLinear,
            "the result carries a usable calibration parameter set");
        Require(fit.BasePitchUm > .6 && fit.BasePitchUm < .9, "the base pitch is resolved from the capacity");

        // A constant-pitch timing record should come back with no invented shape.
        var drive = new DriveTimingCalibrationTests.SimulatedDrive(DriveTimingMedia.Dvd, DriveTimingCacheStrategy.Plain);
        DriveTimingResult record = DriveTimingCalibrator.Measure(drive, new(16, 18, 32, 12, 60));
        DriveTimingCubicResult? flat = DriveTimingCubicFit.TryFit(record, Sectors);
        Require(flat is not null, "a normal timing record can also be fitted");
        Require(Math.Abs(flat!.PitchLinear) + Math.Abs(flat.PitchQuadratic) + Math.Abs(flat.PitchCubic) < .01,
            "a constant-pitch disc does not acquire significant shape coefficients");
        Console.WriteLine("drive-timing-cubic: passed (independent cubic truth recovered, ri scan, record path, flat-disc guard)");
    }

    /// <summary>Independent 40k-point integration of the same p(x) model, kept here rather
    /// than shared with the fitter so a mistake in either grid cannot cancel out.</summary>
    private sealed class CubicTruth
    {
        private readonly double[] _bits;
        private readonly double[] _theta;

        internal CubicTruth(double ri, double ro, double c1, double c2, double c3, double totalBits, double bitMm)
        {
            const int grid = 40_001;
            double dr = (ro - ri) / (grid - 1);
            _bits = new double[grid]; _theta = new double[grid];
            double s = 0, t = 0, previousS = 0, previousT = 0;
            for (int k = 0; k < grid; k++)
            {
                double r = ri + k * dr, x = (double)k / (grid - 1);
                double shape = 1 + x * (c1 + x * (c2 + x * c3));
                double cs = 2 * Math.PI * r / shape, ct = 2 * Math.PI / shape;
                if (k > 0) { s += .5 * (cs + previousS) * dr; t += .5 * (ct + previousT) * dr; }
                previousS = cs; previousT = ct;
                _bits[k] = s; _theta[k] = t;
            }
            double p0 = s / (totalBits * bitMm);
            for (int k = 0; k < grid; k++) { _bits[k] /= p0 * bitMm; _theta[k] /= p0; }
        }

        internal double Theta(double bits)
        {
            int low = 0, high = _bits.Length - 1;
            if (bits <= _bits[0]) return _theta[0];
            if (bits >= _bits[high]) return _theta[high];
            while (high - low > 1)
            {
                int mid = (low + high) / 2;
                if (_bits[mid] <= bits) low = mid; else high = mid;
            }
            double t = (bits - _bits[low]) / (_bits[high] - _bits[low]);
            return _theta[low] + t * (_theta[high] - _theta[low]);
        }
    }

    private static void Require(bool condition, string message)
    {
        if (!condition) throw new InvalidOperationException("drive-timing-cubic: " + message);
    }
}
