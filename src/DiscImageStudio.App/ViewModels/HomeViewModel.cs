using DiscImageStudio.Localization;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.IO;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using DiscImageStudio.BluRay;
using DiscImageStudio.Cd;
using DiscImageStudio.Core;
using DiscImageStudio.Dvd;
using DiscImageStudio.Services;
using MahApps.Metro.IconPacks;
using Microsoft.VisualBasic.FileIO;

namespace DiscImageStudio.ViewModels;

/// <summary>
/// Start page, PR-style: a heading with New/Open, a template row (blank project plus the
/// first presets of each disc family), then the recent-project cards. The page shows the
/// home mode of the shell; all project actions run through the shell.
/// </summary>
public partial class HomeViewModel : ObservableObject
{
    /// <summary>The catalog is user-editable and can grow without bound, while the start
    /// screen keeps its template row to a handful of curated cards. Only the first presets
    /// per family get a card; every preset stays available in the project dialog.</summary>
    private const int PresetsPerFamily = 2;

    private readonly ShellViewModel _shell;
    private readonly DiscParametersState _state;
    private readonly RecentProjectStore _recentStore;

    public HomeViewModel(ShellViewModel shell, DiscParametersState state, RecentProjectStore recentStore)
    {
        _shell = shell;
        _state = state;
        _recentStore = recentStore;
        NewProjectCommand = new AsyncRelayCommand(
            _ => _shell.NewProjectCommand.ExecuteAsync(null),
            () => !_shell.IsBusy);
        OpenProjectCommand = new AsyncRelayCommand(
            _ => _shell.OpenProjectCommand.ExecuteAsync(null),
            () => !_shell.IsBusy);
        ContinueProjectCommand = new RelayCommand(
            () => _shell.NavigateTo(Workspace.Design),
            () => _shell.IsProjectOpen);
        TemplateCommand = new AsyncRelayCommand<HomeTemplateTile>(CreateFromTemplate, _ => !_shell.IsBusy);
        OpenRecentCommand = new AsyncRelayCommand<RecentProjectTile>(OpenRecent, _ => !_shell.IsBusy);
        RemoveRecentCommand = new RelayCommand<RecentProjectTile>(RemoveRecent);
        DeleteRecentCommand = new AsyncRelayCommand<RecentProjectTile>(DeleteRecentAsync, _ => !_shell.IsBusy);
        ClearRecentsCommand = new RelayCommand(ClearRecents, () => HasRecents);
        _shell.BusyChanged += NotifyCommands;
        _shell.PropertyChanged += Shell_PropertyChanged;
        _state.PropertyChanged += State_PropertyChanged;
        BuildTemplates();
        RefreshRecentProjects();
    }

    public ObservableCollection<HomeTemplateTile> Templates { get; } = [];

    public ObservableCollection<RecentProjectTile> Recents { get; } = [];

    public bool HasRecents => Recents.Count > 0;

    /// <summary>True while a project is open and the user came back to the home page; the
    /// page then offers a jump straight back into it.</summary>
    public bool IsProjectOpen => _shell.IsProjectOpen;

    public System.Windows.Visibility IsProjectOpenVisibility => _shell.IsProjectOpen
        ? System.Windows.Visibility.Visible
        : System.Windows.Visibility.Collapsed;

    public string CurrentProjectName => _shell.CurrentProject?.Name ?? string.Empty;

    public IAsyncRelayCommand NewProjectCommand { get; }

    public IAsyncRelayCommand OpenProjectCommand { get; }

    public RelayCommand ContinueProjectCommand { get; }

    public IAsyncRelayCommand<HomeTemplateTile> TemplateCommand { get; }

    public IAsyncRelayCommand<RecentProjectTile> OpenRecentCommand { get; }

    public RelayCommand<RecentProjectTile> RemoveRecentCommand { get; }

    /// <summary>Deletes the project file itself, not just the list entry; the confirm modal
    /// and the Recycle Bin keep a mis-click recoverable.</summary>
    public IAsyncRelayCommand<RecentProjectTile> DeleteRecentCommand { get; }

    /// <summary>The Recycle Bin call has a visible side effect outside the app, so tests swap
    /// it for a plain delete instead of filling the user's own bin.</summary>
    internal Action<string> ProjectFileDeleter { get; set; } = static path =>
        FileSystem.DeleteFile(path, UIOption.OnlyErrorDialogs, RecycleOption.SendToRecycleBin);

    public RelayCommand ClearRecentsCommand { get; }

    /// <summary>Called by the shell whenever the home page becomes visible or the recent
    /// list changes underneath it.</summary>
    internal void RefreshRecentProjects()
    {
        Dictionary<RecentProjectEntry, RecentProjectTile> loaded = Recents.ToDictionary(tile => tile.Entry);
        Recents.Clear();
        foreach (RecentProjectEntry entry in _recentStore.Load())
        {
            Recents.Add(loaded.TryGetValue(entry, out RecentProjectTile? tile)
                ? tile
                : RecentProjectTile.Create(entry));
        }

        OnPropertyChanged(nameof(HasRecents));
        OnPropertyChanged(nameof(IsProjectOpen));
        OnPropertyChanged(nameof(IsProjectOpenVisibility));
        OnPropertyChanged(nameof(CurrentProjectName));
        ContinueProjectCommand.NotifyCanExecuteChanged();
        ClearRecentsCommand.NotifyCanExecuteChanged();
    }

    /// <summary>Rebuilds the cards after a language switch: template tiles and recent tiles
    /// both cache composed text, so they are recreated instead of refreshed in place.</summary>
    internal void Relocalize()
    {
        BuildTemplates();
        Recents.Clear();
        RefreshRecentProjects();
    }

    private void NotifyCommands()
    {
        NewProjectCommand.NotifyCanExecuteChanged();
        OpenProjectCommand.NotifyCanExecuteChanged();
        TemplateCommand.NotifyCanExecuteChanged();
        OpenRecentCommand.NotifyCanExecuteChanged();
        ContinueProjectCommand.NotifyCanExecuteChanged();
    }

    private void Shell_PropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName is nameof(ShellViewModel.IsProjectOpen) or nameof(ShellViewModel.CurrentProject))
        {
            OnPropertyChanged(nameof(IsProjectOpen));
            OnPropertyChanged(nameof(IsProjectOpenVisibility));
            OnPropertyChanged(nameof(CurrentProjectName));
        }
    }

    /// <summary>Preset cards state the project each one starts — CD, DVD or Blu-ray — instead
    /// of repeating the preset's numbers. The catalog is the only source, so user presets read
    /// the same way without a hand-written note.</summary>
    private void BuildTemplates()
    {
        Templates.Clear();
        Templates.Add(HomeTemplateTile.CreateBlank());
        foreach (DvdDiscPreset preset in _state.DvdPresets.Where(value => !value.IsCustom).Take(PresetsPerFamily))
        {
            Templates.Add(CreateTemplate(
                preset.Id, preset.DisplayName, OpticalDiscFamily.Dvd, PackIconLucideKind.Disc,
                preset.Contributor));
        }

        foreach (CdDiscPreset preset in _state.CdPresets.Where(value => !value.IsCustom).Take(PresetsPerFamily))
        {
            Templates.Add(CreateTemplate(
                preset.Id, preset.DisplayName, OpticalDiscFamily.CompactDisc, PackIconLucideKind.Disc3,
                preset.Contributor));
        }

        foreach (BluRayDiscPreset preset in _state.BluRayPresets.Where(value => !value.IsCustom).Take(PresetsPerFamily))
        {
            Templates.Add(CreateTemplate(
                preset.Id, preset.DisplayName, OpticalDiscFamily.BluRay, PackIconLucideKind.DiscAlbum,
                preset.Contributor));
        }
    }

    private static HomeTemplateTile CreateTemplate(
        string presetId,
        string displayName,
        OpticalDiscFamily family,
        PackIconLucideKind icon,
        string? contributor)
    {
        string nature = family switch
        {
            OpticalDiscFamily.Dvd => "DVD",
            OpticalDiscFamily.BluRay => L.T("蓝光"),
            _ => "CD",
        };

        return new HomeTemplateTile(
            presetId,
            StripQualifier(displayName),
            nature,
            icon,
            family,
            IsBlank: false,
            Contributor: contributor);
    }

    private void State_PropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        // The user can add or drop presets in the JSON while the app runs and reload them
        // from the preview page; the template row follows the same catalog.
        if (e.PropertyName is nameof(DiscParametersState.CdPresets)
            or nameof(DiscParametersState.DvdPresets)
            or nameof(DiscParametersState.BluRayPresets))
        {
            BuildTemplates();
        }
    }

    private async Task CreateFromTemplate(HomeTemplateTile? tile)
    {
        if (tile is null || tile.IsBlank)
        {
            // The blank card opens the dialog with its default family; the dialog is where
            // family and parameters are chosen either way.
            await _shell.NewProjectCommand.ExecuteAsync(null);
            return;
        }

        await _shell.RequestNewProjectFromTemplateAsync(tile.Family, tile.PresetId);
    }

    private async Task OpenRecent(RecentProjectTile? tile)
    {
        if (tile is null)
        {
            return;
        }

        RecentProjectEntry entry = tile.Entry;
        if (!File.Exists(entry.ProjectPath))
        {
            _shell.ShowToast(
                L.T("无法打开工程"),
                L.F($"工程文件已移动或删除：\n{entry.ProjectPath}"),
                ToastKind.Warning);
            return;
        }

        // The tile carries the wait: the project header that normally shows progress is on the
        // workspace shell, which is not on screen until the document has been applied.
        tile.IsLoading = true;
        try
        {
            await _shell.OpenProjectFromAsync(entry.ProjectPath);
        }
        finally
        {
            tile.IsLoading = false;
        }
    }

    /// <summary>Right-click removal: drops one entry from the recent list without touching
    /// the project file itself.</summary>
    private void RemoveRecent(RecentProjectTile? tile)
    {
        if (tile is null)
        {
            return;
        }

        _recentStore.Remove(tile.Entry.ProjectPath);
        RefreshRecentProjects();
    }

    private void ClearRecents()
    {
        _recentStore.Clear();
        RefreshRecentProjects();
    }

    /// <summary>Right-click deletion: sends the project file to the Recycle Bin and drops the
    /// entry, so the card and the file both disappear. The open document owns its file through
    /// the save path, and a delete there would be undone by the next Ctrl+S — that case is
    /// refused instead of silently resurrecting the file.</summary>
    private async Task DeleteRecentAsync(RecentProjectTile? tile)
    {
        if (tile is null)
        {
            return;
        }

        string path = tile.Entry.ProjectPath;
        if (string.Equals(path, _shell.ProjectFilePath, StringComparison.OrdinalIgnoreCase))
        {
            _shell.ShowToast(
                L.T("工程正在打开"),
                L.T("请先关闭当前工程，再删除它的文件。"),
                ToastKind.Warning);
            return;
        }

        if (!File.Exists(path))
        {
            _recentStore.Remove(path);
            RefreshRecentProjects();
            _shell.ShowToast(
                L.T("工程文件已不存在"),
                L.F($"{path}\n") + L.T("该条目已从最近工程中移除。"),
                ToastKind.Warning);
            return;
        }

        bool confirmed = await _shell.ConfirmAsync(
            L.T("删除工程文件"),
            L.F($"{path}\n\n") + L.T("文件将被移到回收站，必要时可在资源管理器中还原。"),
            L.T("删除"));
        if (!confirmed)
        {
            return;
        }

        try
        {
            // Recycle Bin rather than a hard delete: the confirm modal is one click away from
            // the harmless list removal, so the file itself stays recoverable.
            ProjectFileDeleter(path);
            _recentStore.Remove(path);
            RefreshRecentProjects();
            _shell.ShowToast(L.T("已删除工程文件"), Path.GetFileName(path), ToastKind.Success);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException)
        {
            // Locked, read-only or gone between the check and the delete: keep the card so the
            // path stays reachable and the reason is visible.
            _shell.ShowToast(L.T("无法删除工程文件"), ex.Message, ToastKind.Error);
        }
    }

    /// <summary>Presets name themselves after what the disc is; a trailing 「（…）」 marks how
    /// the parameters were obtained (实测) and is dropped from the card title, where long names
    /// would be clipped.</summary>
    private static string StripQualifier(string displayName)
    {
        int open = displayName.LastIndexOf('（');
        if (open > 0 && displayName.EndsWith('）'))
        {
            string title = displayName[..open].Trim();
            if (title.Length > 0)
            {
                return title;
            }
        }

        return displayName;
    }
}
