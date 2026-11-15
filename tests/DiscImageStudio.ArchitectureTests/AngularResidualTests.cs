using System.IO;
using DiscImageStudio.Core;
using DiscImageStudio.Core.Calibration;
using DiscImageStudio.Dvd;
using DiscImageStudio.ViewModels;

internal static class AngularResidualTests
{
    internal static void Run()
    {
        TestTableInterpolation();
        TestTableValidation();
        TestTableFromSamples();
        TestGeometryOffsetsAngleOnly();
        TestGeometryInverse();
        TestStraightening();
        TestPresetRoundTrip();
        TestPerpendicularResidualConversion();
        TestFitReportsInjectedBump();
        TestStraightArmYieldsNoTable();
        Console.WriteLine("angular-residual: table interpolation, geometry offsets, "
            + "straightening, preset persistence and fit sign passed");
    }

    private static void TestTableInterpolation()
    {
        var table = AngularResidualTable.Create([new(0, 0), new(0.5, 0.01), new(1, 0.02)]);
        Near(0.01, table.Evaluate(0.5), 1e-15, "knot value is interpolated exactly");
        Near(0, table.Evaluate(-0.2), 1e-15, "value below the first knot is held constant");
        Near(0.02, table.Evaluate(1.4), 1e-15, "value above the last knot is held constant");

        // A constant in x is a global rotation, which the start angle owns.
        var anchored = AngularResidualTable.Create([new(0, 0.3), new(1, 0.4)]);
        Near(0, anchored.Evaluate(0), 1e-15, "the table is anchored to zero at the inner edge");
        Near(0.1, anchored.Evaluate(1), 1e-12, "only the constant part is removed");

        // Monotone data must not overshoot between knots: a PCHIP secant keeps the sign.
        var rising = AngularResidualTable.Create(
            [new(0, 0), new(0.2, 0.001), new(0.4, 0.02), new(0.6, 0.021), new(1, 0.03)]);
        double previous = double.NegativeInfinity;
        for (int i = 0; i <= 2000; i++)
        {
            double value = rising.Evaluate(i / 2000.0);
            Check(value >= previous - 1e-15, "monotone knots give a monotone curve");
            previous = value;
        }

        // A local peak must stay a local peak: PCHIP zeroes the tangent at sign changes.
        var bump = AngularResidualTable.Create([new(0, 0), new(0.5, 0.01), new(1, 0)]);
        Check(bump.Evaluate(0.5) <= 0.01 + 1e-15 && bump.Evaluate(0.75) < 0.01,
            "a local peak does not overshoot its measured value");

        Check(AngularResidualTable.Create([]) is { IsEmpty: true }, "an empty point set is the empty table");
        Check(AngularResidualTable.Create([new(0, 0.01)]) is { IsEmpty: true },
            "a single constant offset is a rotation, so it collapses to the empty table");
    }

    private static void TestTableValidation()
    {
        Throws<ArgumentOutOfRangeException>(() =>
            AngularResidualTable.Create([new(0, 0), new(0.5, 0), new(0.5, 0.01)]),
            "duplicate normalized radii are rejected");
        Throws<ArgumentOutOfRangeException>(() =>
            AngularResidualTable.Create([new(0, 0), new(1.2, 0.01)]),
            "normalized radii outside 0..1 are rejected");
        Throws<ArgumentOutOfRangeException>(() =>
            AngularResidualTable.Create([new(0, 0), new(1, double.NaN)]),
            "non-finite corrections are rejected");
        Throws<ArgumentOutOfRangeException>(() =>
            AngularResidualTable.Create([new(0, 0), new(1, 4 * Math.PI)]),
            "corrections beyond half a turn are rejected");

        // 0.4 rad over 0.02 of a 34 mm band is ~0.59 rad/mm, far above 1/r at the inner edge.
        var steep = AngularResidualTable.Create([new(0, 0), new(0.02, 0.4), new(1, 0)]);
        Throws<ArgumentOutOfRangeException>(() => steep.ValidateMonotonicity(34, 24),
            "a correction steeper than the spiral's angular rate is rejected");
        var gentle = AngularResidualTable.Create([new(0, 0), new(0.5, 0.01), new(1, 0)]);
        gentle.ValidateMonotonicity(34, 24);
    }

    private static void TestTableFromSamples()
    {
        List<AngularResidualSample> samples = [];
        for (int i = 0; i <= 400; i++)
        {
            double x = i / 400.0;
            samples.Add(new(x, 0.004 * Math.Sin(x * Math.PI * 2), 1));
        }

        var table = AngularResidualTable.FromSamples(samples,
            new AngularResidualFitOptions(KnotCount: 32, InnerDensity: 2));
        Check(!table.IsEmpty && table.Count is > 4 and <= 34, "knots are built from scattered samples");
        Near(0, table.Evaluate(0), 1e-15, "sample-built tables are anchored at zero");
        double firstGap = table.Points[1].X - table.Points[0].X;
        double lastGap = table.Points[^1].X - table.Points[^2].X;
        double middleGap = table.Points[17].X - table.Points[16].X;
        Check(firstGap < middleGap / 10, "inner density puts far more knots near the inner edge");
        Check(lastGap < middleGap / 4, "the outer rim keeps enough knots to follow its slower bend");
        Check(firstGap < lastGap, "the inner turns stay the finer of the two ends");
        Near(0.004 * Math.Sin(0.5 * Math.PI * 2), table.Evaluate(0.5), 2e-3,
            "the rebuilt curve follows the measured samples");

        // Waves of the size seen on the first few turns must survive the condensation: a fixed
        // bin width used to average them away against fifteen neighbours on each side.
        List<AngularResidualSample> ripple = [];
        for (int i = 0; i <= 4000; i++)
        {
            double x = i / 4000.0;
            ripple.Add(new(x, 0.0008 * Math.Sin(x * 320), 1));
        }
        var rippled = AngularResidualTable.FromSamples(ripple,
            new AngularResidualFitOptions(KnotCount: 256, InnerDensity: 2, OuterDensity: 2));
        double rippleWorst = 0;
        for (int i = 0; i <= 400; i++)
        {
            double x = 0.02 + 0.04 * i / 400.0;
            rippleWorst = Math.Max(rippleWorst, Math.Abs(rippled.Evaluate(x) - 0.0008 * Math.Sin(x * 320)));
        }
        Check(rippleWorst < 0.0002, $"inner ripples survive the knot condensation: worst {rippleWorst:G4} rad");

        Check(AngularResidualTable.FromSamples([]) is { IsEmpty: true },
            "no samples produce the empty table");
        Throws<ArgumentOutOfRangeException>(() => AngularResidualTable.FromSamples(samples,
            new AngularResidualFitOptions(KnotCount: 1)), "a one-knot table is rejected");
    }

    private static void TestGeometryOffsetsAngleOnly()
    {
        const double inner = 24.1732, outer = 57.8786;
        double length = 2_295_104.0 * 38_688 * 133.3e-6;
        var baseGeometry = DvdTrackGeometry.Create(inner, outer, length, -0.00028, 0.000677, -0.00049);
        var table = AngularResidualTable.Create(
            [new(0, 0), new(0.3, -0.004), new(0.85, -0.012), new(0.95, 0.002), new(1, 0.001)]);
        var corrected = DvdTrackGeometry.Create(inner, outer, length, -0.00028, 0.000677, -0.00049, table);

        Near(baseGeometry.TotalAngleRadians + table.Evaluate(1), corrected.TotalAngleRadians, 1e-12,
            "the reported total angle follows the outer residual");
        for (int i = 0; i <= 200; i++)
        {
            double arc = length * i / 200.0;
            double baseRadius = baseGeometry.RadiusAtArcLengthFast(arc);
            var point = corrected.AtArcLengthFast(arc);
            Near(baseRadius, point.Radius, 1e-9, "the correction never moves the radius");
            double x = (point.Radius - inner) / (outer - inner);
            // 1e-9 tolerates the base model's own segment-interpolation difference.
            Near(0, point.Angle - baseGeometry.AngleAtRadius(point.Radius) - table.Evaluate(x), 1e-9,
                "the forward sample and the radius lookup share one correction");
        }

        // The angle must stay monotone in the track position, which is what the validation guards.
        double previous = double.NegativeInfinity;
        for (int i = 0; i <= 4000; i++)
        {
            double angle = corrected.AtArcLengthFast(length * i / 4000.0).Angle;
            Check(angle > previous, "the corrected angle is strictly increasing");
            previous = angle;
        }
    }

    private static void TestGeometryInverse()
    {
        const double inner = 24.1732, outer = 57.8786;
        double length = 2_295_104.0 * 38_688 * 133.3e-6;
        var table = AngularResidualTable.Create(
            [new(0, 0), new(0.25, 0.003), new(0.7, -0.008), new(1, 0.002)]);
        var geometry = DvdTrackGeometry.Create(inner, outer, length, 0, 0, 0, table);
        for (int i = 1; i < 20; i++)
        {
            double arc = length * i / 20.0;
            double restored = geometry.ArcLengthAtAngle(geometry.AngleAtArcLength(arc));
            Near(arc, restored, 1e-5, "angle-to-arc inversion survives the correction");
        }
    }

    /// <summary>
    /// Reproduces what a burn does: a design line at angle zero is written at the data position
    /// whose model angle equals -R, and lands on the disc at the model angle plus the disc's true
    /// error. With R equal to that error the line comes out straight; without it, it keeps the U.
    /// </summary>
    private static void TestStraightening()
    {
        const double inner = 24.1732, outer = 57.8786;
        double range = outer - inner;
        List<AngularResidualPoint> measured =
        [
            new(0.00, 0.0000),
            new(0.02, 0.0012),
            new(0.04, -0.0009),
            new(0.06, 0.0007),
            new(0.20, -0.0004),
            new(0.50, -0.0015),
            new(0.70, -0.0030),
            new(0.85, -0.0130),
            new(0.93, -0.0090),
            new(0.97, -0.0020),
            new(1.00, 0.0005),
        ];
        var truth = AngularResidualTable.Create(measured);

        double WorstDeviationMm(AngularResidualTable? correction)
        {
            double worst = 0;
            for (int i = 0; i <= 400; i++)
            {
                double radius = inner + range * i / 400.0;
                double x = (radius - inner) / range;
                // The burned position carries the design angle minus the applied correction.
                double applied = correction?.Evaluate(x) ?? 0;
                double landed = -applied + truth.Evaluate(x);
                worst = Math.Max(worst, Math.Abs(landed) * radius);
            }
            return worst;
        }

        double uncorrected = WorstDeviationMm(null);
        double corrected = WorstDeviationMm(truth);
        Check(uncorrected > 0.5, "the simulated disc really deviates by a visible amount");
        Check(corrected < uncorrected / 100, "the measured table straightens the simulated line");
        Near(0, corrected / uncorrected, 0.01, "the leftover deviation is two orders of magnitude smaller");

        // A table with the wrong sign must make things worse, which pins the convention.
        var flipped = AngularResidualTable.Create(
            measured.Select(point => point with { Radians = -point.Radians }));
        Check(WorstDeviationMm(flipped) > uncorrected * 1.5, "subtracting instead of adding doubles the error");
    }

    private static void TestPresetRoundTrip()
    {
        string directory = Path.Combine(Path.GetTempPath(), $"disc-residual-{Guid.NewGuid():N}");
        Directory.CreateDirectory(directory);
        try
        {
            string path = Path.Combine(directory, "presets.json");
            DvdDiscPresetDefinition definition = new("user-dvd-residual", "残差测试", "测角度残差表",
                2_295_104, 24.1732, 57.8786,
                PitchLinear: -0.00028058, PitchQuadratic: 0.00067674, PitchCubic: -0.00049011,
                AngularResidual: [new(0, 0), new(0.5, -0.002), new(0.9, -0.011), new(1, -0.003)]);
            DiscPresetJsonStore.Save(path, new(
                DiscPresetJsonStore.CurrentSchemaVersion, [], [definition], BluRayPresets: []));
            Check(DiscPresetJsonStore.CurrentSchemaVersion == 5, "the preset schema records the residual table");
            DvdDiscPresetDefinition loaded = DiscPresetJsonStore.Load(path).DvdPresets.Single();
            // Collection members compare by reference in a record, so compare the geometry and
            // the table contents separately.
            Check(loaded with { AngularResidual = null } == definition with { AngularResidual = null }
                && loaded.AngularResidual is { Count: 4 }
                && loaded.AngularResidual.SequenceEqual(definition.AngularResidual!),
                "the residual table round-trips through JSON");
            Check(File.ReadAllText(path).Contains("\"angularResidual\""), "the field is written with its camel-case name");

            // A preset without the field keeps working, and an unusable table is refused on read.
            string withoutField = System.Text.RegularExpressions.Regex.Replace(
                File.ReadAllText(path), ",?\\s*\"angularResidual\"\\s*:\\s*\\[[^\\]]*\\]", string.Empty,
                System.Text.RegularExpressions.RegexOptions.Singleline);
            File.WriteAllText(path, withoutField);
            Check(DiscPresetJsonStore.Load(path).DvdPresets.Single().AngularResidual is null,
                "a preset written before the table existed still loads");

            File.WriteAllText(path, withoutField.Replace("\"dvdPresets\": [",
                "\"dvdPresets\": [{ \"id\": \"bad\", \"displayName\": \"bad\", \"description\": \"bad\", "
                + "\"totalSectors\": 2295104, \"innerRadiusMm\": 24.17, \"outerRadiusMm\": 57.88, "
                + "\"angularResidual\": [{ \"x\": 0.5, \"radians\": 0 }, { \"x\": 0.5, \"radians\": 0.01 }] },"));
            Throws<InvalidDataException>(() => DiscPresetJsonStore.Load(path),
                "a table with duplicate knots is rejected with a readable message");

            // The runtime preset carries the table into the shared parameter state.
            var table = AngularResidualTable.Create([new(0, 0), new(0.6, -0.004), new(1, -0.001)]);
            DiscParametersState state = new();
            state.ApplyDvdDiscPreset(new DvdDiscPreset("user-dvd-residual", "残差测试", "测",
                2_295_104, 24.1732, 57.8786, AngularResidual: table));
            Check(ReferenceEquals(state.DvdAngularResidual, table),
                "applying a preset loads its correction into the generation state");
            state.DvdPitchLinear = "0.01";
            Check(ReferenceEquals(state.DvdAngularResidual, table),
                "editing the pitch by hand keeps the measured correction");
        }
        finally
        {
            foreach (string file in Directory.EnumerateFiles(directory)) File.Delete(file);
            Directory.Delete(directory);
        }
    }

    /// <summary>
    /// The fit reports a perpendicular distance; the table stores radians. This pins the whole
    /// convention in one place: divide by the scale, keep the sign, and drop the points whose
    /// scale collapsed because they sit on an almost tangential stretch.
    /// </summary>
    private static void TestPerpendicularResidualConversion()
    {
        const double inner = 24.1732, outer = 57.8786;
        double range = outer - inner;
        // A wide outer dip plus a slow inner wave: the shape the three pitch terms cannot reach.
        double Truth(double x) => -0.009 * Math.Exp(-Math.Pow((x - 0.86) / 0.11, 2))
            + 0.0012 * Math.Sin(x * 30);

        List<AngularResidualMeasurement> measurements = [];
        for (int i = 0; i <= 600; i++)
        {
            double x = i / 600.0, radius = inner + range * x;
            // A point rotated by theta travels radius*theta along the curve, which is exactly
            // what the fitter subtracts; scale is the same radius, so radians come back out.
            measurements.Add(new(x, radius * Truth(x), radius, 1));
        }
        var table = AngularResidualTable.FromPerpendicularResiduals(measurements);
        Check(!table.IsEmpty, "perpendicular residuals produce a table");
        Near(0, table.Evaluate(0), 1e-15, "the conversion anchors at the inner edge");
        double worst = 0;
        for (int i = 0; i <= 600; i++)
        {
            double x = i / 600.0;
            worst = Math.Max(worst, Math.Abs(table.Evaluate(x) - (Truth(x) - Truth(0))));
        }
        Check(worst < 5e-4, $"the conversion recovers the injected shape in radians: worst {worst:G4}");

        // Flipping the sign of the residual must flip the table: subtracting instead of adding
        // would double the error on a burned disc.
        var flipped = AngularResidualTable.FromPerpendicularResiduals(
            measurements.Select(m => m with { ResidualMm = -m.ResidualMm }).ToList());
        Near(-table.Evaluate(0.86), flipped.Evaluate(0.86), 1e-6, "the sign of the residual is kept");

        // A point on a tangential stretch has a vanishing scale; dividing would invent a huge
        // angle, so it must be dropped rather than trusted.
        var degenerate = AngularResidualTable.FromPerpendicularResiduals(
        [
            new(0, 0, 30, 1),
            new(0.5, 0.5, 1e-9, 1),
            new(0.5, 0.002, 30, 1),
            new(1, 0.001, 30, 1),
        ]);
        Check(!degenerate.IsEmpty && Math.Abs(degenerate.MaxAbsRadians) < 0.01,
            "a collapsed angular scale is discarded instead of producing a wild angle");
    }

    /// <summary>
    /// Injects a bump into a synthetic trace of the generated geometry and fits with the pitch
    /// coefficients held. A DVD turn is 0.74 µm, so the free radii alone can still swallow part
    /// of the shape; this test guards the sign and the order of magnitude, while the exact
    /// recovery is pinned by <see cref="TestPerpendicularResidualConversion"/>.
    /// </summary>
    private static void TestFitReportsInjectedBump()
    {
        CalibrationParameters parameters = new(CalibrationDiscKind.Dvd, 24.1732, 57.8786, 2_295_104, 133.3);
        CalibrationTarget target = CalibrationTarget.Create(parameters);
        const double rotation = 0.31;
        double inner = parameters.InnerRadiusMm, range = parameters.OuterRadiusMm - inner;
        double Bump(double radius)
        {
            double x = (radius - inner) / range;
            return -0.009 * Math.Exp(-Math.Pow((x - 0.86) / 0.11, 2)) + 0.0012 * Math.Sin(x * 30);
        }

        // The disc's own geometry is the model geometry: only the arm is rotated by the bump,
        // which is exactly the defect the pitch coefficients cannot describe.
        CalibrationPoint PhysicalPoint(double radius)
            => Polar(radius, -Math.PI / 2 + rotation + Bump(radius));
        CalibrationPoint[] points = Enumerable.Range(0, 129)
            .Select(i => PhysicalPoint(24.3 + 33.4 * i / 128.0)).ToArray();
        CalibrationFitResult fit = CalibrationCrossFitter.Fit(target, [new(0, points)],
            // Locking the radii is the only way to measure a phase defect rather than a shape
            // compromise: one DVD turn is 0.74 um, so a nanometre of radius freedom already buys
            // 0.0005 rad of phase and the radii can synthesise most of the bump themselves.
            new CalibrationFitOptions(FitDvdPitch: false, LockRadii: true));
        Check(fit.Succeeded && fit.BestParameters is not null, "the synthetic trace fits");
        CalibrationParameters best = fit.BestParameters!;
        Check(!fit.AngularResidual.IsEmpty, "the fit reports a residual table");
        Check(best.PitchLinear == 0 && best.PitchCubic == 0,
            "the pitch coefficients stay where the caller pinned them");
        Check(best.InnerRadiusMm == parameters.InnerRadiusMm
            && best.OuterRadiusMm == parameters.OuterRadiusMm,
            "a locked fit leaves both radii untouched");
        Check(!fit.BoundaryLimited, "a locked fit is not reported as boundary-clamped");
        Check(!fit.Warnings.Any(warning => warning.Contains("搜索边界", StringComparison.Ordinal)),
            "a locked fit does not ask the caller to widen a box that is a point by design");

        // With the radii pinned nothing can absorb the bump, so the table must recover most of
        // it. Only the rotation the fit shares with the whole arm is subtracted.
        Check(fit.AngularResidual.Evaluate(0.86) < 0.6 * Bump(inner + range * 0.86),
            $"the outer dip keeps its sign and most of its magnitude: {fit.AngularResidual.Evaluate(0.86):G4} rad");
        Check(fit.AngularResidual.Evaluate(0.86) < fit.AngularResidual.Evaluate(0.2),
            "the dip is recovered where it was injected, not spread over the band");
        Check(fit.AngularResidual.MaxSlopePerMm(range) < 0.5 / inner,
            "the fitted table is gentle enough to keep the spiral monotone");

        // The table must be usable as-is: applying it to a burn has to cancel the arm offset,
        // not merely describe it. A constant phase shift is a rigid rotation of the artwork and
        // is not observable from one arm, so compare the shape with the mean removed.
        double Before = 0, After = 0, plainMean = 0, correctedMean = 0;
        const int steps = 600;
        double[] plainErrors = new double[steps + 1], correctedErrors = new double[steps + 1];
        for (int i = 0; i <= steps; i++)
        {
            double x = i / (double)steps, radius = inner + range * x;
            // What lands on the disc is the true offset minus the correction the geometry adds.
            plainErrors[i] = Bump(radius);
            correctedErrors[i] = Bump(radius) - fit.AngularResidual.Evaluate(x);
            plainMean += plainErrors[i];
            correctedMean += correctedErrors[i];
        }
        plainMean /= steps + 1;
        correctedMean /= steps + 1;
        for (int i = 0; i <= steps; i++)
        {
            Before += Math.Pow(plainErrors[i] - plainMean, 2);
            After += Math.Pow(correctedErrors[i] - correctedMean, 2);
        }
        Check(After < Before * 0.1,
            $"the reported table cancels the injected bend: {Math.Sqrt(After / Before):P0} left");
    }

    /// <summary>
    /// A trace that follows the model exactly must tabulate nothing. Without this the fit can
    /// hand back a plausible-looking curve that is really its own degeneracy, and a preset built
    /// from it would bend a disc that was already straight.
    /// </summary>
    private static void TestStraightArmYieldsNoTable()
    {
        CalibrationParameters parameters = new(CalibrationDiscKind.Dvd, 24.1732, 57.8786, 2_295_104, 133.3);
        CalibrationTarget target = CalibrationTarget.Create(parameters);
        const double rotation = -0.72;
        CalibrationPoint[] points = Enumerable.Range(0, 129)
            .Select(i => Polar(24.3 + 33.4 * i / 128.0, -Math.PI / 2 + rotation)).ToArray();
        CalibrationFitResult fit = CalibrationCrossFitter.Fit(target, [new(0, points)],
            new CalibrationFitOptions(FitDvdPitch: false, LockRadii: true));
        Check(fit.Succeeded, "a straight synthetic trace fits");
        double worst = 0;
        for (int i = 0; i <= 200; i++) worst = Math.Max(worst, Math.Abs(fit.AngularResidual.Evaluate(i / 200.0)));
        Check(worst < 5e-4,
            $"a trace that follows the model tabulates no correction: worst {worst:G4} rad");

        // The same trace with the radii free must not manufacture one either: a radius pair is
        // free to trade against phase, but there is nothing to trade for when the data is exact.
        CalibrationFitResult free = CalibrationCrossFitter.Fit(target, [new(0, points)],
            new CalibrationFitOptions(FitDvdPitch: false));
        double freeWorst = 0;
        for (int i = 0; i <= 200; i++) freeWorst = Math.Max(freeWorst, Math.Abs(free.AngularResidual.Evaluate(i / 200.0)));
        Check(freeWorst < 2e-3,
            $"an exact trace stays empty even with the radii free: worst {freeWorst:G4} rad");
    }

    private static CalibrationPoint Polar(double radius, double angle)
        => new(radius * Math.Cos(angle), radius * Math.Sin(angle));

    private static void Near(double expected, double actual, double tolerance, string message)
        => Check(double.IsFinite(actual) && Math.Abs(expected - actual) <= tolerance,
            $"{message}: expected {expected:G17}, actual {actual:G17}");

    private static void Check(bool value, string message)
    {
        if (!value) throw new InvalidOperationException("angular-residual: " + message);
    }

    private static void Throws<T>(Action action, string message) where T : Exception
    {
        try { action(); }
        catch (T) { return; }
        throw new InvalidOperationException($"angular-residual: expected {typeof(T).Name}; {message}");
    }
}
