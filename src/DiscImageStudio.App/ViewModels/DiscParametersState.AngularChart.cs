using System.Globalization;
using System.Windows;
using System.Windows.Media;
using DiscImageStudio.Core;
using DiscImageStudio.Localization;

namespace DiscImageStudio.ViewModels;

/// <summary>
/// The angular correction table rendered as bindable chart data. The curve lives on the
/// shared state — not on any one page — so the calibration, export, and burn pages all
/// show the same picture of the same table, and an export without correction is visibly
/// different from one with it (the caption says "no table" instead of drawing a curve).
/// </summary>
public partial class DiscParametersState
{
    private const double AngularChartWidth = 268;
    private const double AngularChartHeight = 64;

    /// <summary>Polyline for the correction curve: normalized radius across, angle in
    /// degrees up. Empty when no table is loaded, which is what the caption says too.</summary>
    public PointCollection AngularChartPoints { get; private set; } = [];
    public string AngularChartCaption { get; private set; } = string.Empty;
    public bool HasAngularChart => DvdAngularResidual is { IsEmpty: false };

    partial void OnDvdAngularResidualChanged(AngularResidualTable? value) => RefreshAngularChart();
    partial void OnDvdAngularResidualSourceChanged(string value) => RefreshAngularChart();

    /// <summary>Rebuilds the polyline and its caption from the current table. Runs
    /// automatically when the table or its source label changes (fine calibration, a
    /// preset, a project file) and on language switches so the caption follows the UI.</summary>
    public void RefreshAngularChart()
    {
        if (DvdAngularResidual is not { IsEmpty: false } table)
        {
            AngularChartPoints = [];
            AngularChartCaption = L.T("Angular.None");
        }
        else
        {
            IReadOnlyList<AngularResidualPoint> points = table.Points;
            double min = points.Min(point => point.Radians);
            double max = points.Max(point => point.Radians);
            double span = Math.Max(max - min, 1e-9);
            var chart = new PointCollection(points.Count);
            foreach (AngularResidualPoint point in points)
            {
                double x = Math.Clamp(point.X, 0, 1) * AngularChartWidth;
                double y = AngularChartHeight - (point.Radians - min) / span * AngularChartHeight;
                chart.Add(new Point(x, y));
            }
            AngularChartPoints = chart;
            string source = string.IsNullOrWhiteSpace(DvdAngularResidualSource)
                ? L.T("Angular.FromFineHint")
                : DvdAngularResidualSource;
            AngularChartCaption = L.Format("Angular.Summary", points.Count, source,
                FormatDegrees(min), FormatDegrees(max));
        }
        OnPropertyChanged(nameof(AngularChartPoints));
        OnPropertyChanged(nameof(AngularChartCaption));
        OnPropertyChanged(nameof(HasAngularChart));
    }

    private static string FormatDegrees(double radians) =>
        (radians * 180 / Math.PI).ToString("+0.00;-0.00;0.00", CultureInfo.CurrentCulture);
}
