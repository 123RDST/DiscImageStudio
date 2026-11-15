using System.Globalization;
using System.IO;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using DiscImageStudio.Core;
using DiscImageStudio.Burning;
using DiscImageStudio.Core.Calibration;
using DiscImageStudio.Localization;
using DiscImageStudio.Services;

namespace DiscImageStudio.ViewModels;

public partial class CalibrationViewModel
{
    [ObservableProperty] private bool _useDriveTiming;
    [ObservableProperty] private DriveTimingResult? _timingResult;
    [ObservableProperty] private string _timingStatus = string.Empty;
    /// <summary>DVD timing can also recover the cubic pitch shape, so no second disc is
    /// needed just to remove the residual bend. CD has an unknown along-track origin.</summary>
    [ObservableProperty] private bool _useCubicPitch = true;
    /// <summary>Measurement plan knobs behind the advanced disclosure. The defaults are the
    /// media's historical plan; a denser ladder or a wider span trades minutes for accuracy.</summary>
    [ObservableProperty] private int _timingLadderCount = 22;
    [ObservableProperty] private int _timingSamplesPerRung = 24;
    [ObservableProperty] private int _timingStartPercent = 3;
    [ObservableProperty] private int _timingEndPercent = 85;
    [ObservableProperty] private bool _timingAdvancedOpen;
    /// <summary>Null until the drive has been asked, so the page never claims the tray is
    /// empty just because it has not looked yet.</summary>
    [ObservableProperty] private bool? _timingDiscPresent;
    private string _cdReadSpeed = "1411", _dvdReadSpeed = "5540";
    private int _timingRevision;
    public bool ShowPatternCalibration => !UseDriveTiming;
    /// <summary>The photo stages share one radio group with the one-click method, so a stage
    /// only reads as selected while the photo flow is the active method.</summary>
    public bool ShowFineStageSelection => !UseDriveTiming && IsFineMode;
    public bool ShowCoarseStageSelection => !UseDriveTiming && IsCoarseMode;
    public bool ShowSingleLineStageSelection => !UseDriveTiming && IsIntermediateMode;
    public bool CanSelectDriveTiming => SelectedDiscType != "BD" && !IsBusy;
    public bool CanEditTiming => !IsBusy;
    public bool IsDvdTiming => SelectedDiscType == "DVD";
    public bool CanUseCubicPitch => IsDvdTiming && CanEditTiming;
    public bool HasTimingResult => TimingResult is not null;
    public bool HasTimingDrive => SelectedCapacityRecorder is not null;
    /// <summary>One line of drive state, the way a one-click calibration page reads it.</summary>
    public string TimingDriveState => SelectedCapacityRecorder is not { } device
        ? L.T("Timing.NoDrive")
        : TimingDiscPresent is bool present
            ? L.Format(present ? "Timing.DriveReady" : "Timing.DriveNoDisc", device.DisplayName)
            : L.Format("Timing.DriveDetected", device.DisplayName);
    public string TimingLadderLabel => L.Format("Timing.LadderValue", TimingLadderCount);
    public string TimingSamplesLabel => L.Format("Timing.SamplesValue", TimingSamplesPerRung);
    public string TimingStartLabel => L.Format("Timing.PercentValue", TimingStartPercent);
    public string TimingEndLabel => L.Format("Timing.PercentValue", TimingEndPercent);
    public IReadOnlyList<string> TimingMediaChoices { get; } = ["CD", "DVD"];
    public string TimingReadSpeed
    {
        get => SelectedDiscType == "CD" ? _cdReadSpeed : _dvdReadSpeed;
        set
        {
            if (SelectedDiscType == "CD") _cdReadSpeed = value; else _dvdReadSpeed = value;
            OnPropertyChanged();
        }
    }
    public string TimingTotalSectors
    {
        get => SelectedDiscType == "CD" ? Parameters.CdSectors : Parameters.DvdTotalSectors;
        set
        {
            if (SelectedDiscType == "CD") Parameters.CdSectors = value; else Parameters.DvdTotalSectors = value;
            RefreshTimingProperties();
        }
    }
    public string TimingResultHint => TimingResult is { } result && (result.Media == DriveTimingMedia.Cd ? "CD" : "DVD") != SelectedDiscType
        ? L.T("Timing.MediaMismatch") : TimingResult?.RequiresFineCalibration == true ? L.T("Timing.CdFineRequired") : L.T("Timing.ResultHint");
    public string TimingSummary
    {
        get
        {
            if (TimingResult is not { } result) return string.Empty;
            var range = result.MeasuredRadiusRange;
            string summary = L.Format("Timing.Summary", result.DeviceName, result.ResidualRmsDegrees,
                result.AngleUncertaintyDegrees, range.Inner, range.Outer, result.Targets.Count, result.Media == DriveTimingMedia.Cd ? "CD" : "DVD");
            if (CanApplyTiming())
            {
                var parameters = TimingParameters(result);
                summary += Environment.NewLine + L.Format("Timing.Radii", parameters.InnerRadiusMm, parameters.OuterRadiusMm);
            }
            return summary;
        }
    }
    public RelayCommand SelectDriveTimingCommand { get; private set; } = null!;
    public RelayCommand SelectPatternCalibrationCommand { get; private set; } = null!;
    public IAsyncRelayCommand RefreshTimingReadersCommand { get; private set; } = null!;
    public IAsyncRelayCommand MeasureDriveTimingCommand { get; private set; } = null!;
    /// <summary>The one-click path: record the measurement disc, measure it, and write the
    /// result into the setup without any further choice.</summary>
    public IAsyncRelayCommand StartTimingCalibrationCommand { get; private set; } = null!;
    public IAsyncRelayCommand GenerateTimingImageCommand { get; private set; } = null!;
    public RelayCommand StreamBurnTimingImageCommand { get; private set; } = null!;
    public IAsyncRelayCommand OpenTimingRecordCommand { get; private set; } = null!;
    public RelayCommand SaveTimingRecordCommand { get; private set; } = null!;
    public RelayCommand ApplyTimingResultCommand { get; private set; } = null!;
    /// <summary>Set once when the view applies the recommended timing method. Headless tests
    /// set it themselves to keep the historical photo flow as their default.</summary>
    internal bool TimingDefaultApplied { get; set; }
    /// <summary>Jumps from an applied timing result into the photo fine-calibration stage.</summary>
    public RelayCommand StartFineFromTimingCommand { get; private set; } = null!;
    /// <summary>Stage radios in the method row: picking one also switches to the photo flow.</summary>
    public RelayCommand<string> SelectStageFromMethodRowCommand { get; private set; } = null!;

    private void InitializeTimingCommands()
    {
        SelectDriveTimingCommand = new RelayCommand(() => UseDriveTiming = true, () => CanSelectDriveTiming);
        SelectPatternCalibrationCommand = new RelayCommand(() => UseDriveTiming = false, () => !IsBusy);
        RefreshTimingReadersCommand = new AsyncRelayCommand(RefreshTimingReadersAsync, () => !IsBusy);
        MeasureDriveTimingCommand = new AsyncRelayCommand(MeasureDriveTimingAsync, () => CanSelectDriveTiming && SelectedCapacityRecorder is not null);
        StartTimingCalibrationCommand = new AsyncRelayCommand(StartTimingCalibrationAsync, () => UseDriveTiming && CanSelectDriveTiming);
        GenerateTimingImageCommand = new AsyncRelayCommand(GenerateTimingImageAsync, () => CanSelectDriveTiming);
        StreamBurnTimingImageCommand = new RelayCommand(PrepareTimingStreamBurn,
            () => UseDriveTiming && CanSelectDriveTiming);
        OpenTimingRecordCommand = new AsyncRelayCommand(OpenTimingRecordAsync, () => !IsBusy);
        SaveTimingRecordCommand = new RelayCommand(SaveTimingRecord, () => !IsBusy && TimingResult is not null);
        ApplyTimingResultCommand = new RelayCommand(ApplyTimingResult, () => !IsBusy && CanApplyTiming());
        StartFineFromTimingCommand = new RelayCommand(StartFineFromTiming, () => !IsBusy && HasTimingResult);
        SelectStageFromMethodRowCommand = new RelayCommand<string>(SelectStageFromMethodRow, _ => !IsBusy);
    }

    private void StartFineFromTiming()
    {
        UseDriveTiming = false;
        SelectFineModeCommand.Execute(null);
        RefreshTimingProperties();
    }

    private void SelectStageFromMethodRow(string? stage)
    {
        // Stage first, method second: the row's radio group is driven by the combined flags,
        // so flipping the method while the stage is still "fine" would light the fine radio
        // and leave it on screen after the stage actually changed.
        SelectStage(stage switch
        {
            "coarse" => CalibrationStage.Coarse,
            "single" => CalibrationStage.SingleLine,
            _ => CalibrationStage.Fine,
        });
        UseDriveTiming = false;
        RefreshTimingProperties();
    }

    partial void OnUseDriveTimingChanged(bool value)
    {
        if (value && SelectedDiscType == "BD") { UseDriveTiming = false; return; }
        OnPropertyChanged(nameof(ShowPatternCalibration));
        OnPropertyChanged(nameof(ShowFineStageSelection));
        OnPropertyChanged(nameof(ShowCoarseStageSelection));
        OnPropertyChanged(nameof(ShowSingleLineStageSelection));
        RefreshCommands();
    }
    partial void OnTimingResultChanged(DriveTimingResult? value) => RefreshTimingProperties();
    partial void OnUseCubicPitchChanged(bool value) => RefreshTimingProperties();
    partial void OnTimingDiscPresentChanged(bool? value) => OnPropertyChanged(nameof(TimingDriveState));
    partial void OnTimingLadderCountChanged(int value) => OnPropertyChanged(nameof(TimingLadderLabel));
    partial void OnTimingSamplesPerRungChanged(int value) => OnPropertyChanged(nameof(TimingSamplesLabel));
    partial void OnTimingStartPercentChanged(int value) => OnPropertyChanged(nameof(TimingStartLabel));
    partial void OnTimingEndPercentChanged(int value) => OnPropertyChanged(nameof(TimingEndLabel));

    private bool CanApplyTiming()
    {
        if (TimingResult is not { } result || (result.Media == DriveTimingMedia.Cd ? "CD" : "DVD") != SelectedDiscType) return false;
        try { _ = TimingParameters(result); return true; }
        catch (ArgumentException) { return false; }
        catch (OverflowException) { return false; }
    }

    private DriveTimingCubicResult? _cubicCache;
    private (DriveTimingResult Result, long Capacity, bool Enabled)? _cubicCacheKey;

    /// <summary>The cubic fit builds thousands of spiral grids, so share one result between
    /// the summary, the status line, the apply guard and the actual application.</summary>
    private DriveTimingCubicResult? CubicFit(DriveTimingResult result)
    {
        if (!UseCubicPitch || result.Media != DriveTimingMedia.Dvd) return null;
        long capacity;
        try { capacity = ReadTimingCapacity(); }
        catch (ArgumentException) { return null; }
        if (_cubicCacheKey is { } key && ReferenceEquals(key.Result, result)
            && key.Capacity == capacity && key.Enabled) return _cubicCache;
        _cubicCache = DriveTimingCubicFit.TryFit(result, capacity);
        _cubicCacheKey = (result, capacity, true);
        return _cubicCache;
    }

    private CalibrationParameters TimingParameters(DriveTimingResult result)
    {
        long capacity = ReadTimingCapacity();
        return (CubicFit(result)?.Parameters ?? result.ToParameters(capacity)) with
        {
            DvdReadCompatibility = Parameters.DvdReadCompatibility,
            UsableSectors = Parameters.GetUsableSectors(result.Media == DriveTimingMedia.Cd
                ? OpticalDiscFamily.CompactDisc : OpticalDiscFamily.Dvd),
        };
    }

    /// <summary>Shown next to the result: whether the pitch shape was recovered, and how well.</summary>
    public string CubicPitchStatus
    {
        get
        {
            if (TimingResult is not { } result) return string.Empty;
            if (result.Media != DriveTimingMedia.Dvd) return L.T("Timing.CubicPitchCd");
            if (!UseCubicPitch) return L.T("Timing.CubicPitchOff");
            DriveTimingCubicResult? cubic = CubicFit(result);
            if (cubic is null) return L.T("Timing.CubicPitchFailed");
            return L.Format("Timing.CubicPitchOn", cubic.PitchLinear, cubic.PitchQuadratic,
                cubic.PitchCubic, cubic.RmsDegrees);
        }
    }

    private long ReadTimingCapacity()
    {
        if (!long.TryParse(TimingTotalSectors, NumberStyles.None, CultureInfo.InvariantCulture, out long sectors)
            || sectors is < 8192 or > 3_000_000 || (SelectedDiscType == "CD" && sectors > 450_000))
            throw new ArgumentException(L.T("Timing.InvalidCapacity"));
        return sectors;
    }

    private Task RefreshTimingReadersAsync() => RunAsync(L.T("Timing.FindingDrives"), async token =>
    {
        string? previous = SelectedCapacityRecorder?.Id;
        var devices = await _shell.OpticalBurner.GetDevicesAsync(token);
        token.ThrowIfCancellationRequested();
        CapacityRecorders = devices;
        SelectedCapacityRecorder = devices.FirstOrDefault(d => d.Id == previous) ?? (devices.Count == 1 ? devices[0] : null);
        await ProbeTimingMediaAsync(token);
        TimingStatus = _statusMessage = L.T(devices.Count == 0 ? "CalibrationMedia.NoDevices"
            : SelectedCapacityRecorder is null ? "CalibrationMedia.PickDevice" : "Timing.Ready");
    });

    /// <summary>Page-load probe: enumerates drives and asks about the disc without taking the
    /// busy state or opening a dock job, so a host without a dispatcher cannot be driven off
    /// its own thread. Failures are silent — the page simply shows no drive.</summary>
    internal async Task ProbeTimingDrivesAsync()
    {
        try
        {
            string? previous = SelectedCapacityRecorder?.Id;
            var devices = await _shell.OpticalBurner.GetDevicesAsync(CancellationToken.None);
            _shell.OnUiThread(() =>
            {
                CapacityRecorders = devices;
                SelectedCapacityRecorder = devices.FirstOrDefault(d => d.Id == previous)
                    ?? (devices.Count == 1 ? devices[0] : null);
            });
            await ProbeTimingMediaAsync(CancellationToken.None);
        }
        catch (Exception) { }
    }

    /// <summary>Ask the drive whether a readable disc is loaded. A refusal is how a drive
    /// says "no disc", so a failure here is reported as an empty drive, not as an error.</summary>
    private async Task ProbeTimingMediaAsync(CancellationToken token)
    {
        if (SelectedCapacityRecorder is not { } device) { TimingDiscPresent = null; return; }
        try
        {
            await _shell.OpticalBurner.GetMediaSectorCountAsync(device.Id,
                SelectedDiscType == "CD" ? OpticalBurnMediaKind.CdData : OpticalBurnMediaKind.DvdData, token);
            TimingDiscPresent = true;
        }
        catch (OperationCanceledException) { throw; }
        catch (Exception) { TimingDiscPresent = false; }
    }

    private async Task MeasureDriveTimingAsync()
    {
        if (SelectedCapacityRecorder is not { } recorder) return;
        if (!ushort.TryParse(TimingReadSpeed, NumberStyles.None, CultureInfo.InvariantCulture, out ushort speed))
        { Report(new ArgumentException(L.T("Timing.InvalidSpeed"))); return; }
        string family = SelectedDiscType;
        int revision = ++_timingRevision;
        await RunAsync(L.T("Timing.Measuring"), async token =>
        {
            TimingResult = await RunTimingMeasurementAsync(recorder, family, speed, revision, token, 0);
            TimingStatus = _statusMessage = L.T("Timing.Measured");
            _shell.AppendLog(TimingSummary + Environment.NewLine);
        });
    }

    /// <summary>One click: record the measurement disc on the selected drive, read it back, and
    /// write the fitted geometry into the setup. The plan comes from the advanced panel.</summary>
    private async Task StartTimingCalibrationAsync()
    {
        if (SelectedCapacityRecorder is not { } selected)
        {
            await RefreshTimingReadersAsync();
            if (SelectedCapacityRecorder is not { } found) { TimingStatus = L.T("Timing.NoDrive"); return; }
            selected = found;
        }
        OpticalBurnDevice recorder = selected;
        if (!ushort.TryParse(TimingReadSpeed, NumberStyles.None, CultureInfo.InvariantCulture, out ushort speed))
        { Report(new ArgumentException(L.T("Timing.InvalidSpeed"))); return; }
        TimingMeasurementSource source;
        try { source = CaptureTimingMeasurement(); }
        catch (Exception ex) { Report(ex); return; }
        string family = SelectedDiscType;
        int revision = ++_timingRevision;
        await RunAsync(L.T("Timing.OneClickRunning"), async token =>
        {
            TimingStatus = L.Format("Timing.Burning", 0);
            _shell.ReportDockJob(TimingStatus, 0);
            OpticalMediaCapacity capacity = await _shell.OpticalBurner.GetMediaCapacityAsync(recorder.Id, source.MediaKind, token);
            OpticalWriteSpeed? writeSpeed = await PickTimingWriteSpeedAsync(recorder.Id, source.MediaKind, token);
            OpticalBurnRequest request = source.CreateBurnRequest(recorder.Id, writeSpeed, capacity.WritableSectors);
            long total = Math.Max(1, request.ContentLength);
            var burnProgress = new Progress<OpticalBurnProgress>(p =>
            {
                if (token.IsCancellationRequested || revision != _timingRevision || _cancellation?.Token != token) return;
                double share = Math.Clamp((double)p.CompletedBytes / total, 0, 1);
                TimingStatus = L.Format("Timing.Burning", Math.Round(share * 100));
                _shell.ReportDockJob(TimingStatus, share * .5);
            });
            await _shell.OpticalBurner.BurnAsync(request, burnProgress, token);
            token.ThrowIfCancellationRequested();
            _shell.AppendLog(L.Format("Timing.BurnFinished", request.ContentLength / (1024.0 * 1024.0),
                writeSpeed?.GetMultiplier(source.MediaKind).ToString("0.#", CultureInfo.InvariantCulture) ?? "auto") + Environment.NewLine);
            TimingDiscPresent = true;
            DriveTimingResult result = await RunTimingMeasurementAsync(recorder, family, speed, revision, token, .5);
            TimingResult = result;
            if (CanApplyTiming()) { ApplyTimingResult(); TimingStatus = _statusMessage = L.T("Timing.OneClickDone"); }
            else TimingStatus = _statusMessage = L.T("Timing.Measured");
            _shell.AppendLog(TimingSummary + Environment.NewLine);
        });
    }

    /// <summary>Fastest supported write speed, or automatic when the drive will not say.</summary>
    private async Task<OpticalWriteSpeed?> PickTimingWriteSpeedAsync(string deviceId, OpticalBurnMediaKind media,
        CancellationToken token)
    {
        try
        {
            var speeds = await _shell.OpticalBurner.GetSupportedWriteSpeedsAsync(deviceId, media, token);
            return speeds.OrderByDescending(s => s.SectorsPerSecond).FirstOrDefault();
        }
        catch (NotSupportedException) { return null; }
        catch (InvalidOperationException) { return null; }
    }

    private async Task<DriveTimingResult> RunTimingMeasurementAsync(OpticalBurnDevice recorder, string family,
        ushort speed, int revision, CancellationToken token, double jobOffset)
    {
        var progress = new Progress<DriveTimingProgress>(p =>
        {
            if (token.IsCancellationRequested || revision != _timingRevision || _cancellation?.Token != token) return;
            TimingStatus = FormatTimingProgress(p);
            double share = p.Total > 0 ? Math.Clamp((double)p.Completed / p.Total, 0, 1) : 0;
            _shell.ReportDockJob(TimingStatus, jobOffset + (1 - jobOffset) * share);
        });
        var result = await _shell.OpticalBurner.ReadDiscAsync(recorder.Id, speed, session =>
        {
            var reader = new DriveTimingReader(session);
            if ((reader.Disc.Media == DriveTimingMedia.Cd ? "CD" : "DVD") != family)
                throw new InvalidOperationException(L.T("Timing.WrongMedia"));
            return DriveTimingCalibrator.Measure(reader, TimingOptions(), progress, token);
        }, token);
        token.ThrowIfCancellationRequested();
        if (family != SelectedDiscType || recorder.Id != SelectedCapacityRecorder?.Id) throw new OperationCanceledException();
        return result;
    }

    /// <summary>The measurement plan the advanced panel describes.</summary>
    internal DriveTimingOptions TimingOptions() => new(SamplesPerTarget: TimingSamplesPerRung,
        MaximumTargets: TimingLadderCount, StartFraction: TimingStartPercent / 100.0,
        EndFraction: TimingEndPercent / 100.0);

    /// <summary>Reset the plan to the family's default when the family changes.</summary>
    internal void ApplyTimingOptionDefaults(string? family)
    {
        DriveTimingOptions plan = DriveTimingOptions.For(family == "CD" ? DriveTimingMedia.Cd : DriveTimingMedia.Dvd);
        TimingLadderCount = plan.MaximumTargets;
        TimingSamplesPerRung = plan.SamplesPerTarget;
        TimingStartPercent = (int)Math.Round(plan.StartFraction * 100);
        TimingEndPercent = (int)Math.Round(plan.EndFraction * 100);
    }

    internal TimingMeasurementSource CaptureTimingMeasurement()
    {
        DriveTimingMedia media = SelectedDiscType switch
        {
            "CD" => DriveTimingMedia.Cd,
            "DVD" => DriveTimingMedia.Dvd,
            _ => throw new ArgumentException(L.T("Timing.UnsupportedMedia")),
        };
        long capacity = ReadTimingCapacity();
        long usable = Parameters.GetUsableSectors(SelectedDiscType == "CD"
            ? OpticalDiscFamily.CompactDisc : OpticalDiscFamily.Dvd);
        // Cap the plain-data image at its closing allowance without deducting
        // another reserve from an already explicit usable-sector limit.
        uint sectors = checked((uint)(SelectedDiscType == "CD" ? Math.Min(usable, capacity - 250) : usable));
        var source = new TimingMeasurementSource(media, sectors, DateTimeOffset.UtcNow);
        _ = source.ContentLength;
        return source;
    }

    private void PrepareTimingStreamBurn()
    {
        if (!UseDriveTiming || !CanSelectDriveTiming) return;
        try
        {
            _shell.CalibrationBurn.PrepareTimingMeasurement(CaptureTimingMeasurement());
            _shell.NavigateTo(Workspace.CalibrationBurn);
        }
        catch (Exception ex) { Report(ex); }
    }

    private async Task GenerateTimingImageAsync()
    {
        TimingMeasurementSource source;
        try { source = CaptureTimingMeasurement(); }
        catch (Exception ex) { Report(ex); return; }
        string? path = _shell.Dialogs?.PickSave(L.T("光盘镜像|*.iso"), ".iso", "timing-measurement.iso");
        if (path is null) return;
        await GenerateTimingImageToAsync(path, source);
    }

    internal Task GenerateTimingImageToAsync(string path, TimingMeasurementSource source) => RunAsync(L.T("Timing.CreatingImage"), async token =>
    {
        int revision = ++_timingRevision;
        string target = Path.GetFullPath(path), staging = target + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try
        {
            var progress = new Progress<double>(p => { if (!token.IsCancellationRequested && revision == _timingRevision && _cancellation?.Token == token) _shell.ReportDockJob(L.T("Timing.CreatingImage"), p); });
            await Task.Run(() =>
            {
                using var stream = new FileStream(staging, FileMode.CreateNew, FileAccess.Write);
                source.Write(stream, token, progress);
                stream.Flush(flushToDisk: true);
            }, token);
            token.ThrowIfCancellationRequested();
            File.Move(staging, target, overwrite: true);
            TimingStatus = _statusMessage = L.Format("Timing.ImageSaved", target);
        }
        finally { JobDiagnostics.TryCleanup(() => File.Delete(staging), _shell.AppendLog, L.T("Timing.CreatingImage")); }
    });

    private async Task OpenTimingRecordAsync()
    {
        string? path = _shell.Dialogs?.PickOpen(title: L.T("Timing.OpenRecord"), filter: L.T("Timing.RecordFilter"));
        if (path is not null) await LoadTimingRecordFromAsync(path);
    }
    internal Task LoadTimingRecordFromAsync(string path) => RunAsync(L.T("Timing.OpenRecord"), async token =>
    {
        var result = await Task.Run(() => DriveTimingRecord.Load(path), token);
        token.ThrowIfCancellationRequested();
        SelectedDiscType = result.Media == DriveTimingMedia.Cd ? "CD" : "DVD";
        TimingResult = result;
        UseDriveTiming = true;
        TimingStatus = _statusMessage = L.T("Timing.RecordLoaded");
    });
    private void SaveTimingRecord()
    {
        if (TimingResult is not { } result) return;
        string? path = _shell.Dialogs?.PickSave(L.T("Timing.RecordFilter"), ".timing-calibration.json", "measurement.timing-calibration.json");
        if (path is null) return;
        try { DriveTimingRecord.Save(path, result); TimingStatus = _statusMessage = L.Format("Timing.RecordSaved", path); }
        catch (Exception ex) { Report(ex); }
    }
    private void ApplyTimingResult()
    {
        if (!CanApplyTiming() || TimingResult is not { } result) return;
        try
        {
            WriteSetupParameters(TimingParameters(result));
            // The angular table describes the spindle (eccentricity and speed error), not the
            // radii the timing run measured. Dropping it here silently brought the outer-edge
            // U-shaped lines back, so it is kept and reported instead.
            if (Parameters.DvdAngularResidual is { IsEmpty: false } kept)
                _shell.AppendLog(L.Format("Timing.AngularKept", kept.Points.Count) + Environment.NewLine);
            else
                _shell.AppendLog(L.T("Timing.AngularMissing") + Environment.NewLine);
            SelectStage(CalibrationStage.Fine);
            TimingStatus = _statusMessage = L.T(result.RequiresFineCalibration ? "Timing.AppliedCd" : "Timing.Applied");
            _shell.AppendLog(_statusMessage + Environment.NewLine);
        }
        catch (Exception ex) { Report(ex); }
    }
    private static string FormatTimingProgress(DriveTimingProgress p) => p.Stage switch
    {
        DriveTimingStage.CheckingCache => L.T("Timing.CheckingCache"),
        DriveTimingStage.CheckingRotation => L.Format("Timing.CheckingRotation", p.Completed + 1, p.Total),
        DriveTimingStage.Settling => L.T("Timing.Settling"),
        DriveTimingStage.Measuring => L.Format("Timing.TargetProgress", p.Completed + 1, p.Total),
        DriveTimingStage.Complete => L.T("Timing.Measured"),
        _ => L.T("Timing.CheckingMedia"),
    };
    private void RefreshTimingProperties()
    {
        foreach (string name in new[] { nameof(TimingTotalSectors), nameof(TimingReadSpeed), nameof(TimingSummary), nameof(TimingResultHint), nameof(CubicPitchStatus), nameof(CanUseCubicPitch), nameof(IsDvdTiming), nameof(HasTimingResult), nameof(CanSelectDriveTiming), nameof(CanEditTiming), nameof(HasTimingDrive), nameof(TimingDriveState), nameof(ShowFineStageSelection), nameof(ShowCoarseStageSelection), nameof(ShowSingleLineStageSelection) })
            OnPropertyChanged(name);
        RefreshTimingCommands();
    }
    private void RefreshTimingCommands()
    {
        OnPropertyChanged(nameof(CanSelectDriveTiming)); OnPropertyChanged(nameof(CanEditTiming));
        SelectDriveTimingCommand?.NotifyCanExecuteChanged(); SelectPatternCalibrationCommand?.NotifyCanExecuteChanged();
        RefreshTimingReadersCommand?.NotifyCanExecuteChanged(); MeasureDriveTimingCommand?.NotifyCanExecuteChanged();
        StartTimingCalibrationCommand?.NotifyCanExecuteChanged();
        GenerateTimingImageCommand?.NotifyCanExecuteChanged(); OpenTimingRecordCommand?.NotifyCanExecuteChanged();
        StreamBurnTimingImageCommand?.NotifyCanExecuteChanged();
        SaveTimingRecordCommand?.NotifyCanExecuteChanged(); ApplyTimingResultCommand?.NotifyCanExecuteChanged();
        StartFineFromTimingCommand?.NotifyCanExecuteChanged();
        SelectStageFromMethodRowCommand?.NotifyCanExecuteChanged();
    }
}
