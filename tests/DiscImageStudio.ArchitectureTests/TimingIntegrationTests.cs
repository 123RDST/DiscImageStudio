using System.Globalization;
using System.IO;
using DiscImageStudio.Core;
using DiscImageStudio.Core.Calibration;
using DiscImageStudio.Services;
using DiscImageStudio.ViewModels;
using System.Windows.Threading;

/// <summary>End-to-end checks for the ported timing method on this branch: a simulated spindle
/// result is applied to the calibration setup, a measurement disc is captured, the burn page
/// switches to its plain-data source, and the result card leads into fine calibration.
/// No recorder is touched.</summary>
internal static class TimingIntegrationTests
{
    internal static void Run()
    {
        string directory = Path.Combine(Path.GetTempPath(), "disc-timing-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        try
        {
            var shell = new ShellViewModel(Dispatcher.CurrentDispatcher);
            var calibration = shell.Calibration;
            calibration.TimingDefaultApplied = true; // keep the view's recommended default out of the model test
            calibration.SelectedDiscType = "DVD";

            var drive = new DriveTimingCalibrationTests.SimulatedDrive(DriveTimingMedia.Dvd, DriveTimingCacheStrategy.Plain);
            DriveTimingResult result = DriveTimingCalibrator.Measure(drive, new(16, 18, 32, 12, 60));
            result.Validate();
            calibration.TimingResult = result;

            // The record must survive a save/load round trip before it is applied.
            string record = Path.Combine(directory, "measurement.timing-calibration.json");
            DriveTimingRecord.Save(record, result);
            DriveTimingResult loaded = DriveTimingRecord.Load(record);
            Require(loaded.Targets.Count == result.Targets.Count, "a timing record round-trips its targets");

            long capacity = long.Parse(calibration.TimingTotalSectors, NumberStyles.None, CultureInfo.InvariantCulture);
            CalibrationParameters expected = calibration.UseCubicPitch
                ? DriveTimingCubicFit.TryFit(result, capacity)?.Parameters ?? result.ToParameters(capacity)
                : result.ToParameters(capacity);
            calibration.ApplyTimingResultCommand.Execute(null);
            Require(calibration.IsFineMode, "applying a timing result selects the fine stage");
            Require(Math.Abs(double.Parse(calibration.Parameters.DvdInnerRadius, CultureInfo.InvariantCulture) - expected.InnerRadiusMm) < 1e-9
                && Math.Abs(double.Parse(calibration.Parameters.DvdOuterRadius, CultureInfo.InvariantCulture) - expected.OuterRadiusMm) < 1e-9,
                "applying a timing result writes its geometry into the setup");
            Require(calibration.CubicPitchStatus.Length > 0, "the result reports its cubic pitch fit status");
            calibration.UseCubicPitch = false;
            calibration.ApplyTimingResultCommand.Execute(null);
            Require(Math.Abs(double.Parse(calibration.Parameters.DvdInnerRadius, CultureInfo.InvariantCulture)
                - result.ToParameters(capacity).InnerRadiusMm) < 1e-9,
                "turning the cubic fit off falls back to the constant-pitch radii");
            calibration.UseCubicPitch = true;

            // The angular table describes the spindle, not the radii, so applying a timing
            // result must keep it: dropping it silently brought the outer-edge U lines back.
            calibration.Parameters.DvdAngularResidual =
                AngularResidualTable.Create([new(0, 0), new(.5, .01), new(1, .02)]);
            Require(calibration.HasAngularResidual, "a table measured by the fine stage is visible in the setup");
            Require(calibration.AngularResidualChartPoints.Count == 3
                && calibration.AngularResidualCaption.Contains('3'),
                "the loaded table is drawn as a curve and labelled with its point count");
            calibration.ApplyTimingResultCommand.Execute(null);
            Require(calibration.HasAngularResidual,
                "applying a timing result keeps the measured angular table");

            // The advanced disclosure is the measurement plan: the sliders must reach the
            // calibrator, and the window must actually bound where the rungs are taken.
            calibration.SelectedDiscType = "CD";
            Require(calibration.TimingLadderCount == 36 && calibration.TimingSamplesPerRung == 32,
                "switching to CD restores the CD plan defaults");
            calibration.SelectedDiscType = "DVD";
            Require(calibration.TimingLadderCount == 22 && calibration.TimingSamplesPerRung == 24,
                "switching back to DVD restores the DVD plan defaults");
            calibration.TimingLadderCount = 30;
            calibration.TimingSamplesPerRung = 12;
            calibration.TimingStartPercent = 6;
            calibration.TimingEndPercent = 60;
            DriveTimingOptions plan = calibration.TimingOptions();
            Require(plan.MaximumTargets == 30 && plan.SamplesPerTarget == 12
                && Math.Abs(plan.StartFraction - .06) < 1e-9 && Math.Abs(plan.EndFraction - .6) < 1e-9,
                "the advanced panel becomes the measurement plan");
            Require(calibration.TimingLadderLabel == "30" && calibration.TimingStartLabel.StartsWith("6", StringComparison.Ordinal),
                "the sliders read back their value next to the label");
            DriveTimingResult narrow = DriveTimingCalibrator.Measure(
                new DriveTimingCalibrationTests.SimulatedDrive(DriveTimingMedia.Dvd, DriveTimingCacheStrategy.Plain),
                plan with { MaximumTargets = 18, SamplesPerTarget = 16 });
            long recorded = narrow.RecordedSectors;
            Require(narrow.Targets.All(t => t.Lba >= recorded * .06 - 16 && t.Lba <= recorded * .6),
                "the inner and outer sliders bound every rung");
            Require(narrow.Targets.Count > 8, "a narrowed plan still produces enough rungs");
            calibration.TimingLadderCount = 22;
            calibration.TimingSamplesPerRung = 24;
            calibration.TimingStartPercent = 3;
            calibration.TimingEndPercent = 85;

            // One measurement disc: a plain closed data image, no calibration pattern.
            TimingMeasurementSource source = calibration.CaptureTimingMeasurement();
            Require(source.Media == DriveTimingMedia.Dvd && source.Sectors >= 8192, "the measurement source is a DVD data disc");
            Require(source.ContentLength == (long)source.Sectors * 2048, "the measurement image length matches its sector count");
            // Writing the full 4.7 GB to memory would overflow; a small disc exercises the
            // same deterministic ISO + random-data writer.
            using var image = new MemoryStream();
            var small = source with { Sectors = 8192 };
            small.Write(image, CancellationToken.None);
            Require(image.Length == small.ContentLength, "the measurement image has the exact announced length");

            shell.CalibrationBurn.PrepareTimingMeasurement(source);
            Require(shell.CalibrationBurn.IsTimingMeasurement, "the burn page knows it is writing a measurement disc");
            Require(shell.CalibrationBurn.IsDvdBurn && !shell.CalibrationBurn.IsBluRayBurn,
                "the burn page follows the measurement disc's family");
            shell.CalibrationBurn.UpdateSourceSummary();
            Require(shell.CalibrationBurn.SourceTitle.Contains("DVD"), "the burn page describes the measurement disc");

            shell.CalibrationBurn.PrepareCalibrationPattern();
            Require(!shell.CalibrationBurn.IsTimingMeasurement, "leaving timing restores the calibration pattern source");

            // The method row shares one radio group: picking a coarse stage from the timing
            // page must land on that stage, not fall back to the fine one.
            calibration.UseDriveTiming = true;
            calibration.SelectStageFromMethodRowCommand.Execute("coarse");
            Require(!calibration.UseDriveTiming && calibration.IsCoarseMode
                && calibration.ShowCoarseStageSelection && !calibration.ShowFineStageSelection,
                "a stage picked in the method row switches to the photo flow and stays selected");

            calibration.UseDriveTiming = true;
            calibration.StartFineFromTimingCommand.Execute(null);
            Require(!calibration.UseDriveTiming && calibration.IsFineMode,
                "the result card jumps into the photo fine-calibration stage");
        }
        finally { Directory.Delete(directory, recursive: true); }
        Console.WriteLine("timing-integration: passed (simulated result, record round trip, applied geometry, measurement image, burn source and fine-stage handoff)");
    }

    private static void Require(bool condition, string message)
    {
        if (!condition) throw new InvalidOperationException("timing-integration: " + message);
    }
}
