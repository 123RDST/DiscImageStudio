namespace DiscImageStudio.Core;

/// <summary>
/// One vertex of a measured angular correction, keyed by normalized radius
/// x = (r - ri) / (ro - ri) and stored in radians. Positive values mean the burned
/// pattern sits ahead of (counter-clockwise of) the position the track model predicted.
/// </summary>
public sealed record AngularResidualPoint(double X, double Radians);

/// <summary>One measurement feeding <see cref="AngularResidualTable.FromSamples"/>.</summary>
public sealed record AngularResidualSample(double NormalizedRadius, double Radians, double Weight = 1);

/// <summary>
/// One curve-fit residual, expressed the way a fit measures it: a perpendicular distance from
/// the predicted curve, together with the radial distance one radian of phase buys there.
/// </summary>
public sealed record AngularResidualMeasurement(
    double NormalizedRadius, double ResidualMm, double MmPerRadian, double Weight = 1);

public sealed record AngularResidualFitOptions(
    int KnotCount = 40,
    double InnerDensity = 2,
    double OuterDensity = 1.4,
    int SmoothingPasses = 0)
{
    public void Validate()
    {
        if (KnotCount is < 2 or > AngularResidualTable.MaxKnots)
            throw new ArgumentOutOfRangeException(nameof(KnotCount),
                $"节点数必须在 2–{AngularResidualTable.MaxKnots} 之间。");
        if (!double.IsFinite(InnerDensity) || InnerDensity is < 1 or > 6)
            throw new ArgumentOutOfRangeException(nameof(InnerDensity), "内圈加密指数必须在 1–6 之间。");
        if (!double.IsFinite(OuterDensity) || OuterDensity is < 1 or > 6)
            throw new ArgumentOutOfRangeException(nameof(OuterDensity), "外圈加密指数必须在 1–6 之间。");
        if (SmoothingPasses is < 0 or > 8)
            throw new ArgumentOutOfRangeException(nameof(SmoothingPasses), "平滑次数必须在 0–8 之间。");
    }
}

/// <summary>
/// A measured correction to the spiral angle, tabulated against normalized radius and
/// interpolated with a monotone cubic Hermite (PCHIP) spline.
/// </summary>
/// <remarks>
/// <para>The three pitch coefficients cannot express a local bump: x, x² and x³ describe a
/// one-parameter family of nearly identical shapes once the total track length fixes the
/// radial growth (the fit basis has a condition number around 126). Measured curvature that
/// survives the pitch fit is therefore stored directly, in the quantity it was measured in —
/// angle — instead of being pushed back through another ill-conditioned radial fit.</para>
/// <para>Knots may be placed unevenly, which lets the inner few turns carry fine detail
/// (three turns span only 2 mm of a 34 mm drawing area) while the outer region uses a
/// coarse spacing.</para>
/// <para>The table is anchored so that its value at x = 0 is zero: an offset constant in x
/// is a pure rotation of the whole pattern, which the start angle already owns. Everything
/// that consumes the table can therefore keep <see cref="DvdTrackGeometry.TotalAngleRadians"/>
/// consistent by adding the value at x = 1.</para>
/// </remarks>
public sealed class AngularResidualTable : IEquatable<AngularResidualTable>
{
    public const int MaxKnots = 256;

    /// <summary>Half a turn. Anything larger is a tracking failure, not a pitch correction.</summary>
    public const double LimitRadians = Math.PI;

    private readonly double[] _x;
    private readonly double[] _y;
    private readonly double[] _width;
    private readonly double[] _tangent;
    private readonly double _maxAbsSlope;

    private AngularResidualTable(double[] x, double[] y)
    {
        _x = x;
        _y = y;
        _width = new double[Math.Max(0, x.Length - 1)];
        _tangent = new double[x.Length];
        for (int i = 0; i < _width.Length; i++) _width[i] = x[i + 1] - x[i];
        ComputeTangents();
        _maxAbsSlope = MeasureMaxSlope();
    }

    public static AngularResidualTable Empty { get; } = new([], []);

    public IReadOnlyList<AngularResidualPoint> Points => _x.Length == 0 ? [] : BuildPoints();

    public bool IsEmpty => _x.Length == 0;

    public int Count => _x.Length;

    /// <summary>Knot-by-knot value equality. Presets are records, so their residual member has
    /// to compare by content: without this, re-reading the preset catalogue would hand the UI a
    /// "different" table for the same entry and silently drop the user's selection.</summary>
    public bool Equals(AngularResidualTable? other)
        => other is not null
            && _x.AsSpan().SequenceEqual(other._x)
            && _y.AsSpan().SequenceEqual(other._y);

    public override bool Equals(object? obj) => obj is AngularResidualTable other && Equals(other);

    public override int GetHashCode()
    {
        HashCode hash = new();
        foreach (double value in _x) hash.Add(value);
        foreach (double value in _y) hash.Add(value);
        return hash.ToHashCode();
    }

    /// <summary>Largest correction magnitude on the table, in radians.</summary>
    public double MaxAbsRadians => _y.Length == 0 ? 0 : _y.Max(Math.Abs);

    /// <summary>Largest |dR/dx| over the whole table, per unit of normalized radius.</summary>
    public double MaxSlopePerNormalizedRadius => _maxAbsSlope;

    /// <summary>
    /// Upper bound on how fast the correction changes along the track, in radians per mm.
    /// The generation path uses it to widen its sampling-reuse margin.
    /// </summary>
    public double MaxSlopePerMm(double radialRangeMm)
        => double.IsFinite(radialRangeMm) && radialRangeMm > 0 ? _maxAbsSlope / radialRangeMm : 0;

    public static AngularResidualTable Create(IEnumerable<AngularResidualPoint> points)
    {
        ArgumentNullException.ThrowIfNull(points);
        List<AngularResidualPoint> ordered = points
            .OrderBy(point => point.X)
            .ToList();
        if (ordered.Count == 0) return Empty;

        double[] x = new double[ordered.Count];
        double[] y = new double[ordered.Count];
        for (int i = 0; i < ordered.Count; i++)
        {
            AngularResidualPoint point = ordered[i];
            if (!double.IsFinite(point.X) || point.X < 0 || point.X > 1)
                throw new ArgumentOutOfRangeException(nameof(points), "残差节点的归一化半径必须在 0–1 之间。");
            if (!double.IsFinite(point.Radians) || Math.Abs(point.Radians) > LimitRadians)
                throw new ArgumentOutOfRangeException(nameof(points), "残差节点的角度必须有限且不超过半圈。");
            if (i > 0 && point.X <= x[i - 1])
                throw new ArgumentOutOfRangeException(nameof(points), "残差节点的归一化半径必须严格递增。");
            x[i] = point.X;
            y[i] = point.Radians;
        }

        // A constant offset is a global rotation: the start angle already provides it.
        double anchor = y[0];
        for (int i = 0; i < y.Length; i++) y[i] -= anchor;
        if (y.All(value => Math.Abs(value) < 1e-12)) return Empty;
        return new AngularResidualTable(x, y);
    }

    /// <summary>
    /// Builds a table from scattered measurements by averaging them into knots. Knot spacing is
    /// warped so both ends get proportionally more resolution — the inner turns are crowded
    /// (three turns span only 2 mm of a 34 mm band) and so is the outer rim, where the track
    /// model is least able to follow the real spiral. The ends are held flat where no data
    /// exists, because a flat segment is a rotation there and leaves the artwork continuous.
    /// </summary>
    public static AngularResidualTable FromSamples(
        IReadOnlyList<AngularResidualSample> samples,
        AngularResidualFitOptions? options = null)
    {
        ArgumentNullException.ThrowIfNull(samples);
        options ??= new();
        options.Validate();
        List<AngularResidualSample> valid = samples
            .Where(sample => double.IsFinite(sample.NormalizedRadius) && double.IsFinite(sample.Radians)
                && double.IsFinite(sample.Weight) && sample.Weight > 0
                && sample.NormalizedRadius >= 0 && sample.NormalizedRadius <= 1)
            .ToList();
        if (valid.Count == 0) return Empty;
        double first = valid.Min(sample => sample.NormalizedRadius);
        double last = valid.Max(sample => sample.NormalizedRadius);

        int count = Math.Max(2, options.KnotCount);
        double[] centers = new double[count];
        for (int index = 0; index < count; index++)
            centers[index] = Warp(index / (double)(count - 1), options);
        if (!(centers[1] > centers[0])) return Empty;

        // Nearest-knot bins keep every knot reading only its own neighbourhood. A fixed-width
        // window would average the densely packed inner knots over a wide span and flatten
        // exactly the small inner wobble the fine inner spacing exists to capture.
        double[] weights = new double[count];
        double[] totals = new double[count];
        foreach (AngularResidualSample sample in valid)
        {
            int index = NearestCenter(centers, sample.NormalizedRadius);
            weights[index] += sample.Weight;
            totals[index] += sample.Weight * sample.Radians;
        }
        List<AngularResidualPoint> knots = [];
        for (int index = 0; index < count; index++)
            if (weights[index] > 0) knots.Add(new AngularResidualPoint(centers[index], totals[index] / weights[index]));
        if (knots.Count == 0) return Empty;
        var extendedKnots = ExtendToEnds(knots, first, last, valid);
        return Smooth(extendedKnots, options.SmoothingPasses);
    }

    /// <summary>
    /// Maps uniform knot index to normalized radius with an S-shaped density:
    /// u^p / (u^p + (1 - u)^q) is flat near both ends and steep in the middle, and its endpoint
    /// values are exactly 0 and 1. <paramref name="inner"/> above <paramref name="outer"/>
    /// keeps the inner turns finer than the outer rim.
    /// </summary>
    private static double Warp(double u, AngularResidualFitOptions options)
    {
        if (u <= 0) return 0;
        if (u >= 1) return 1;
        double inner = Math.Pow(u, options.InnerDensity);
        double outer = Math.Pow(1 - u, options.OuterDensity);
        double total = inner + outer;
        return total > 0 ? inner / total : u;
    }

    /// <summary>
    /// Converts fitted curve residuals into an angle table. Dividing the perpendicular distance
    /// by <see cref="AngularResidualMeasurement.MmPerRadian"/> is the whole convention: a
    /// residual ahead of the predicted curve is a positive phase, and the geometry adds the
    /// table to the model angle.
    /// </summary>
    public static AngularResidualTable FromPerpendicularResiduals(
        IReadOnlyList<AngularResidualMeasurement> measurements,
        AngularResidualFitOptions? options = null)
    {
        ArgumentNullException.ThrowIfNull(measurements);
        List<AngularResidualSample> samples = new(measurements.Count);
        foreach (AngularResidualMeasurement measurement in measurements)
        {
            if (!double.IsFinite(measurement.ResidualMm) || !double.IsFinite(measurement.MmPerRadian)) continue;
            // A point measured on an almost tangential stretch makes the scale collapse, and
            // dividing by it would turn a rounding error into a wild angle. Such a point carries
            // no usable shape, so it is dropped rather than clamped.
            if (!(measurement.MmPerRadian > 0)) continue;
            double radians = measurement.ResidualMm / measurement.MmPerRadian;
            if (!double.IsFinite(radians) || Math.Abs(radians) > LimitRadians / 8) continue;
            if (!double.IsFinite(measurement.NormalizedRadius)
                || measurement.NormalizedRadius < 0 || measurement.NormalizedRadius > 1) continue;
            if (!double.IsFinite(measurement.Weight) || measurement.Weight <= 0) continue;
            samples.Add(new AngularResidualSample(measurement.NormalizedRadius, radians, measurement.Weight));
        }
        return FromSamples(samples, options);
    }

    private static int NearestCenter(double[] centers, double value)
    {
        int index = Array.BinarySearch(centers, value);
        if (index >= 0) return index;
        int upper = ~index;
        if (upper <= 0) return 0;
        if (upper >= centers.Length) return centers.Length - 1;
        return value - centers[upper - 1] <= centers[upper] - value ? upper - 1 : upper;
    }

    /// <summary>
    /// Correction in radians at a normalized radius. Values outside the tabulated span are held
    /// constant, which keeps the generation path monotone and the artwork continuous.
    /// </summary>
    public double Evaluate(double normalizedRadius)
    {
        if (_x.Length == 0) return 0;
        if (!double.IsFinite(normalizedRadius)) return 0;
        if (_x.Length == 1 || normalizedRadius <= _x[0]) return _y[0];
        if (normalizedRadius >= _x[^1]) return _y[^1];
        int index = Array.BinarySearch(_x, normalizedRadius);
        if (index >= 0) return _y[index];
        int lower = ~index - 1;
        double t = (normalizedRadius - _x[lower]) / _width[lower];
        double h = _width[lower];
        // Hermite basis in the form used by DvdTrackGeometry.Hermite.
        return _y[lower] + t * (h * _tangent[lower]
            + t * (3 * (_y[lower + 1] - _y[lower]) - 2 * h * _tangent[lower] - h * _tangent[lower + 1]
                + t * (h * _tangent[lower] + h * _tangent[lower + 1] - 2 * (_y[lower + 1] - _y[lower]))));
    }

    public double EvaluateAtRadius(double radiusMm, double innerRadiusMm, double outerRadiusMm)
    {
        double range = outerRadiusMm - innerRadiusMm;
        if (!double.IsFinite(range) || range <= 0) return 0;
        return Evaluate((radiusMm - innerRadiusMm) / range);
    }

    /// <summary>Value at the outer edge; the geometry adds it to its reported total angle.</summary>
    public double OuterValueRadians => _x.Length == 0 ? 0 : _y[^1];

    /// <summary>
    /// Rejects tables whose slope would make the spiral angle non-monotone. The angle grows at
    /// 1/r per mm of travel, so the correction must stay well below that everywhere.
    /// </summary>
    public void ValidateMonotonicity(double radialRangeMm, double innerRadiusMm)
    {
        if (_x.Length == 0) return;
        if (!double.IsFinite(radialRangeMm) || radialRangeMm <= 0 || innerRadiusMm <= 0) return;
        double limit = 0.5 / innerRadiusMm;
        double slope = MaxSlopePerMm(radialRangeMm);
        if (slope > limit)
            throw new ArgumentOutOfRangeException(nameof(radialRangeMm),
                $"角度残差表在内圈的斜率 {slope:G4} rad/mm 过高，会使螺旋角非单调（上限 {limit:G4} rad/mm）。");
    }

    private IReadOnlyList<AngularResidualPoint> BuildPoints()
    {
        AngularResidualPoint[] points = new AngularResidualPoint[_x.Length];
        for (int i = 0; i < points.Length; i++) points[i] = new AngularResidualPoint(_x[i], _y[i]);
        return points;
    }

    private void ComputeTangents()
    {
        int n = _x.Length;
        if (n == 0) return;
        if (n == 1) { _tangent[0] = 0; return; }
        double[] secant = new double[n - 1];
        for (int i = 0; i < n - 1; i++) secant[i] = (_y[i + 1] - _y[i]) / _width[i];
        _tangent[0] = EndpointTangent(secant[0], secant.Length > 1 ? secant[1] : secant[0], _width[0],
            _width.Length > 1 ? _width[1] : _width[0]);
        _tangent[n - 1] = EndpointTangent(secant[^1], secant.Length > 1 ? secant[^2] : secant[^1],
            _width[^1], _width.Length > 1 ? _width[^2] : _width[^1]);
        for (int i = 1; i < n - 1; i++)
        {
            // A sign change means a local extremum: a zero tangent there prevents overshoot.
            if (secant[i - 1] * secant[i] <= 0) { _tangent[i] = 0; continue; }
            double w1 = 2 * _width[i] + _width[i - 1];
            double w2 = _width[i] + 2 * _width[i - 1];
            _tangent[i] = (w1 + w2) / (w1 / secant[i - 1] + w2 / secant[i]);
        }
    }

    private static double EndpointTangent(double first, double second, double wide, double narrow)
    {
        double tangent = ((2 * wide + narrow) * first - wide * second) / (wide + narrow);
        if (!double.IsFinite(tangent) || tangent * first <= 0) return 0;
        if (first * second <= 0 && Math.Abs(tangent) > 3 * Math.Abs(first)) return 3 * first;
        return tangent;
    }

    private double MeasureMaxSlope()
    {
        if (_x.Length < 2) return 0;
        double maximum = 0;
        for (int i = 0; i < _width.Length; i++)
        {
            double h = _width[i], y0 = _y[i], y1 = _y[i + 1], m0 = _tangent[i], m1 = _tangent[i + 1];
            for (int step = 0; step <= 8; step++)
            {
                double t = step / 8.0;
                double derivative = m0 + t * (2 * (3 * (y1 - y0) / h - 2 * m0 - m1)
                    + 3 * t * (m0 + m1 - 2 * (y1 - y0) / h));
                maximum = Math.Max(maximum, Math.Abs(derivative));
            }
        }
        return maximum;
    }

    private static List<AngularResidualPoint> ExtendToEnds(List<AngularResidualPoint> knots,
        double first, double last, List<AngularResidualSample> samples)
    {
        List<AngularResidualPoint> extended = [];
        if (first > 0) extended.Add(new(0, WeightedMean(samples, sample => sample.NormalizedRadius <= first)));
        extended.AddRange(knots);
        if (last < 1) extended.Add(new(1, WeightedMean(samples, sample => sample.NormalizedRadius >= last)));
        // Remove duplicate X values produced by knots that already sit on an end.
        List<AngularResidualPoint> deduped = [];
        foreach (AngularResidualPoint point in extended.OrderBy(point => point.X))
            if (deduped.Count == 0 || point.X > deduped[^1].X + 1e-9) deduped.Add(point);
        return deduped;
    }

    private static double WeightedMean(List<AngularResidualSample> samples, Func<AngularResidualSample, bool> select)
    {
        double weight = 0, sum = 0;
        foreach (AngularResidualSample sample in samples)
            if (select(sample)) { weight += sample.Weight; sum += sample.Weight * sample.Radians; }
        return weight > 0 ? sum / weight : 0;
    }

    private static AngularResidualTable Smooth(List<AngularResidualPoint> knots, int passes)
    {
        if (passes <= 0 || knots.Count < 3) return Create(knots);
        double[] values = knots.Select(knot => knot.Radians).ToArray();
        for (int pass = 0; pass < passes; pass++)
        {
            double[] smoothed = (double[])values.Clone();
            for (int i = 1; i < values.Length - 1; i++)
                smoothed[i] = 0.25 * values[i - 1] + 0.5 * values[i] + 0.25 * values[i + 1];
            values = smoothed;
        }
        return Create(knots.Select((knot, index) => knot with { Radians = values[index] }));
    }
}
