using System.Windows;
using System.Windows.Controls;
using DiscImageStudio.ViewModels;

namespace DiscImageStudio.Views;

public partial class DriveTimingCalibrationView : UserControl
{
    private bool _probed;

    public DriveTimingCalibrationView()
    {
        InitializeComponent();
        Loaded += (_, _) => ProbeOnce();
        IsVisibleChanged += (_, e) => { if (e.NewValue is true) ProbeOnce(); };
    }

    // A one-click page cannot open with an empty drive list and a manual "find drives"
    // button. Probe once when the page first becomes visible; the advanced panel keeps the
    // manual refresh for a drive that is connected later. Collapsed pages never touch the
    // drives, so the photo flow and headless checks pay nothing for this.
    private void ProbeOnce()
    {
        if (_probed || !IsVisible || DataContext is not CalibrationViewModel calibration) return;
        _probed = true;
        if (!calibration.HasTimingDrive) _ = calibration.ProbeTimingDrivesAsync();
    }
}
