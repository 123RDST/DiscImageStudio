using DiscImageStudio.Core;
using DiscImageStudio.Core.Calibration;
using DiscImageStudio.ViewModels;

internal static class ProjectSessionTests
{
    internal static void Run()
    {
        TestBluRayRoundtrip();
        TestFamilyIsolation();
        TestAngularTableTravelsWithProject();
        Console.WriteLine("project-session: passed (BD apply/capture, tone normalization, polarity, family isolation, angular table)");
    }

    private static void TestBluRayRoundtrip()
    {
        ProjectDocument document = Document(
            ProjectDocument.BluRayFamily,
            new ProjectGeometry(23.98822105616562, 58.023234731100764, 1024),
            new ProjectOutputs(@"C:\out\bd.iso", false, @"C:\data", "BD_IMAGE", false),
            new ToneProfile(0.4, ToneProfile.FourLevels, 6, SrgbLinearization: true),
            new ProjectBluRaySettings(InvertMapping: true));
        DiscParametersState state = new();
        ProjectSession.ApplyToState(document, state);

        Require(state.BdInnerRadius == "23.98822105616562", "BD inner radius");
        Require(state.BdOuterRadius == "58.023234731100764", "BD outer radius");
        Require(state.BdTotalSectors == "1024", "BD sectors");
        Require(state.BdActualInnerRadius == state.BdInnerRadius, "BD measured geometry starts neutral");
        Require(state.BdOutputPath == @"C:\out\bd.iso" && state.BdVolumeLabel == "BD_IMAGE", "BD outputs");
        Require(state.BdInvertMapping, "BD polarity");
        Require(state.ToneLevels == ToneProfile.TwoLevels, "BD tone is binary");
        Require(state.ToneSrgbLinearization && Math.Abs(state.ToneBrightness - 6) < 1e-12, "BD gamma and brightness");

        state.BdInnerRadius = "24.1";
        state.BdOuterRadius = "57.9";
        state.BdTotalSectors = "2048";
        state.BdOutputPath = @"C:\out\edited.iso";
        state.BdInvertMapping = false;
        ProjectGeometry captured = ProjectSession.CaptureGeometry(document, state);
        Require(captured.InnerRadiusMm == 24.1 && captured.OuterRadiusMm == 57.9
            && captured.Sectors == 2048 && captured.PitchLinear == 0, "BD geometry capture");
        Require(ProjectSession.CaptureBluRay(document, state)?.InvertMapping == false, "BD polarity capture");
        Require(ProjectSession.CaptureTone(document, state).Levels == ToneProfile.TwoLevels,
            "BD capture keeps two levels");
    }


    /// <summary>A project carries the measured angular table, and opening one written before
    /// that field existed must not erase the table already loaded: exporting or burning from
    /// the emptied state bakes a bent outer edge into the disc.</summary>
    private static void TestAngularTableTravelsWithProject()
    {
        AngularResidualTable table = AngularResidualTable.Create(
        [
            new AngularResidualPoint(0.0, 0.0),
            new AngularResidualPoint(0.5, 0.012),
            new AngularResidualPoint(1.0, 0.023),
        ]);

        DiscParametersState source = new();
        source.DvdAngularResidual = table;
        ProjectDocument withTable = Document(
            ProjectDocument.DvdFamily,
            new ProjectGeometry(24.173255717923812, 57.9139952472366, 2_298_496,
                AngularResidual: [.. table.Points]),
            new ProjectOutputs(@"C://out//dvd.iso", false, string.Empty, "DVD_IMAGE", false),
            ToneProfile.LegacyBinary,
            BluRay: null);
        ProjectGeometry captured = ProjectSession.CaptureGeometry(withTable, source);
        Require(captured.AngularResidual is { Count: 3 }, "the measured table is captured with the project");

        DiscParametersState reopened = new();
        ProjectSession.ApplyToState(withTable with { Geometry = captured }, reopened);
        Require(reopened.DvdAngularResidual is { IsEmpty: false } restored && restored.Points.Count == 3,
            "reopening restores the table");

        // A legacy project (no angular field) must keep the loaded table instead of clearing it.
        DiscParametersState loaded = new() { DvdAngularResidual = table };
        ProjectDocument legacy = withTable with
        {
            Geometry = new ProjectGeometry(24.173255717923812, 57.9139952472366, 2_298_496),
        };
        ProjectSession.ApplyKeepingAngular(legacy, loaded);
        Require(loaded.DvdAngularResidual is { IsEmpty: false },
            "opening a project without a table keeps the loaded one");
        Require(loaded.DvdAngularResidualSource.Length > 0, "and labels where the kept table came from");
    }


    private static void TestFamilyIsolation()
    {
        ProjectDocument cd = Document(
            ProjectDocument.CdFamily,
            new ProjectGeometry(24.5, 56.8, 359_849),
            new ProjectOutputs(@"C:\out\cd.wav", true, string.Empty, string.Empty, true),
            new ToneProfile(0.5, ToneProfile.FourLevels, 0),
            BluRay: null);
        DiscParametersState state = new();
        ProjectSession.ApplyToState(cd, state);
        Require(state.CdSectors == "359849" && state.ToneLevels == ToneProfile.FourLevels,
            "CD session keeps four levels");
        Require(ProjectSession.CaptureBluRay(cd, state) is null, "CD project has no BD section");
        Require(ProjectSession.CaptureTone(cd, state).Levels == ToneProfile.FourLevels,
            "CD capture keeps four levels");

        ProjectDocument dvd = Document(
            ProjectDocument.DvdFamily,
            new ProjectGeometry(24.0, 58.0, 2_295_104, 0.001, 0, 0),
            new ProjectOutputs(@"C:\out\dvd.iso", false, string.Empty, "DISC_IMAGE", false),
            new ToneProfile(0.5, ToneProfile.FourLevels, 0),
            BluRay: null);
        ProjectSession.ApplyToState(dvd, state);
        Require(state.DvdTotalSectors == "2295104" && state.ToneLevels == ToneProfile.TwoLevels,
            "DVD session normalizes four levels to two");
    }

    private static ProjectDocument Document(
        string family, ProjectGeometry geometry, ProjectOutputs outputs, ToneProfile tone,
        ProjectBluRaySettings? BluRay)
        => new(
            ProjectJsonStore.CurrentSchemaVersion,
            "session-test",
            family,
            geometry,
            [],
            outputs,
            CalibrationSessionPath: null,
            ThumbnailPngBase64: null,
            CreatedUtc: DateTimeOffset.UnixEpoch,
            ModifiedUtc: DateTimeOffset.UnixEpoch,
            Tone: tone,
            BluRay: BluRay);

    private static void Require(bool condition, string message)
    {
        if (!condition)
        {
            throw new InvalidOperationException("Project session: " + message + ".");
        }
    }
}
