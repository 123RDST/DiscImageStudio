using System.Collections.ObjectModel;
using System.IO;
using CommunityToolkit.Mvvm.Input;
using DiscImageStudio.Core;

namespace DiscImageStudio.ViewModels;

/// <summary>
/// The disc's accompanying files: a list of folders and loose files instead of the single
/// folder the page used to take. This half owns the pick/remove commands and the watchers that
/// keep the blue file ring honest when a folder changes underneath us.
/// </summary>
public partial class DesignViewModel
{
    private readonly List<FileSystemWatcher> _dataSourceWatchers = [];
    private bool _dataSourcesHooked;
    private RelayCommand? _addDataFilesCommand;
    private RelayCommand? _addDataFoldersCommand;
    private RelayCommand<string>? _removeDataSourceCommand;
    private RelayCommand<string>? _moveDataSourceUpCommand;
    private RelayCommand<string>? _moveDataSourceDownCommand;
    private RelayCommand? _clearDataSourcesCommand;

    /// <summary>The open family's list, which is what the page shows and the pipelines read.</summary>
    public ObservableCollection<string> CurrentDataSources => Family switch
    {
        OpticalDiscFamily.Dvd => _state.DvdDataSources,
        OpticalDiscFamily.BluRay => _state.BdDataSources,
        _ => _state.CdDataSources,
    };

    public bool HasDataSources => CurrentDataSources.Count > 0;

    /// <summary>Inverse of <see cref="HasDataSources"/>; WPF has no built-in inverse
    /// visibility converter and the empty-state hint needs one.</summary>
    public bool HasNoDataSources => CurrentDataSources.Count == 0;

    public RelayCommand AddDataFilesCommand =>
        _addDataFilesCommand ??= new RelayCommand(AddDataFiles, () => !_shell.IsBusy);

    public RelayCommand AddDataFoldersCommand =>
        _addDataFoldersCommand ??= new RelayCommand(AddDataFolders, () => !_shell.IsBusy);

    public RelayCommand<string> RemoveDataSourceCommand =>
        _removeDataSourceCommand ??= new RelayCommand<string>(RemoveDataSource, _ => !_shell.IsBusy);

    /// <summary>Moves an entry one slot up. The list order is the order the filesystem writes
    /// the files, so this decides which file sits at the inner edge. The button greys out on
    /// the first and last row instead of silently doing nothing.</summary>
    public RelayCommand<string> MoveDataSourceUpCommand =>
        _moveDataSourceUpCommand ??= new RelayCommand<string>(MoveDataSourceUp, CanMoveUp);

    public RelayCommand<string> MoveDataSourceDownCommand =>
        _moveDataSourceDownCommand ??= new RelayCommand<string>(MoveDataSourceDown, CanMoveDown);

    public RelayCommand ClearDataSourcesCommand =>
        _clearDataSourcesCommand ??= new RelayCommand(ClearDataSources,
            () => !_shell.IsBusy && CurrentDataSources.Count > 0);

    /// <summary>Checked when the open family's list carries the signature file. Toggling adds
    /// or removes the entry like any pick, so it can be reordered or deleted row by row.</summary>
    public bool AutoReadme
    {
        get => CurrentDataSources.Any(DiscParametersState.IsAutoReadme);
        set
        {
            if (_shell.IsBusy) return;
            ObservableCollection<string> sources = CurrentDataSources;
            int index = IndexOfAutoReadme(sources);
            if (value && index < 0)
            {
                DiscParametersState.EnsureAutoReadmeFile();
                sources.Add(DiscParametersState.AutoReadmePath);
            }
            else if (!value)
            {
                while (index >= 0)
                {
                    sources.RemoveAt(index);
                    index = IndexOfAutoReadme(sources);
                }
            }
        }
    }

    private static int IndexOfAutoReadme(ObservableCollection<string> sources)
    {
        for (int i = 0; i < sources.Count; i++)
            if (DiscParametersState.IsAutoReadme(sources[i])) return i;
        return -1;
    }

    private int IndexOfSource(string? path)
        => string.IsNullOrEmpty(path) ? -1 : CurrentDataSources.IndexOf(path);

    private bool CanMoveUp(string? path) => !_shell.IsBusy && IndexOfSource(path) > 0;

    private bool CanMoveDown(string? path)
    {
        int index = IndexOfSource(path);
        return !_shell.IsBusy && index >= 0 && index < CurrentDataSources.Count - 1;
    }

    /// <summary>Hooks the three lists once and arms the watchers. Safe to call repeatedly: the
    /// project loader assigns sources without going through a command.</summary>
    internal void ConnectDataSources()
    {
        if (!_dataSourcesHooked)
        {
            _dataSourcesHooked = true;
            _state.CdDataSources.CollectionChanged += (_, _) => OnDataSourcesChanged();
            _state.DvdDataSources.CollectionChanged += (_, _) => OnDataSourcesChanged();
            _state.BdDataSources.CollectionChanged += (_, _) => OnDataSourcesChanged();
        }

        RefreshDataSourceWatchers();
        NotifyDataSourcesChanged();
    }

    private void OnDataSourcesChanged()
    {
        RefreshDataSourceWatchers();
        NotifyDataSourcesChanged();
        // Adding or removing a pick moves the blue ring just like editing a folder's
        // contents does; the plan is debounced, so a bulk load runs it once.
        ScheduleDataRegion();
    }

    private void NotifyDataSourcesChanged()
    {
        OnPropertyChanged(nameof(HasDataSources));
        OnPropertyChanged(nameof(HasNoDataSources));
        OnPropertyChanged(nameof(AutoReadme));
        _clearDataSourcesCommand?.NotifyCanExecuteChanged();
        _addDataFilesCommand?.NotifyCanExecuteChanged();
        _addDataFoldersCommand?.NotifyCanExecuteChanged();
        // Row boundaries moved: the top row's "up" and the last row's "down" re-evaluate.
        _moveDataSourceUpCommand?.NotifyCanExecuteChanged();
        _moveDataSourceDownCommand?.NotifyCanExecuteChanged();
    }

    private void AddDataFiles()
    {
        if (_shell.IsBusy) return;
        string[]? picked = _shell.Dialogs?.PickDataFiles();
        if (picked is null) return;
        foreach (string path in picked) DiscParametersState.AddSource(CurrentDataSources, path);
    }

    private void AddDataFolders()
    {
        if (_shell.IsBusy) return;
        string[]? picked = _shell.Dialogs?.PickDataFolders();
        if (picked is null) return;
        foreach (string path in picked) DiscParametersState.AddSource(CurrentDataSources, path);
    }

    private void RemoveDataSource(string? path)
    {
        if (_shell.IsBusy || string.IsNullOrEmpty(path)) return;
        CurrentDataSources.Remove(path);
    }

    private void MoveDataSourceUp(string? path)
    {
        if (_shell.IsBusy || string.IsNullOrEmpty(path)) return;
        ObservableCollection<string> sources = CurrentDataSources;
        int index = sources.IndexOf(path);
        if (index > 0) sources.Move(index, index - 1);
    }

    private void MoveDataSourceDown(string? path)
    {
        if (_shell.IsBusy || string.IsNullOrEmpty(path)) return;
        ObservableCollection<string> sources = CurrentDataSources;
        int index = sources.IndexOf(path);
        if (index >= 0 && index < sources.Count - 1) sources.Move(index, index + 1);
    }

    private void ClearDataSources()
    {
        if (_shell.IsBusy) return;
        CurrentDataSources.Clear();
    }

    /// <summary>Re-arms one watcher per folder source. Loose file picks are not watched: their
    /// own size changes are picked up the next time the guide is planned, and watching the
    /// parent folder of every file would scan unrelated directories.</summary>
    internal void RefreshDataSourceWatchers()
    {
        foreach (FileSystemWatcher watcher in _dataSourceWatchers) watcher.Dispose();
        _dataSourceWatchers.Clear();
        foreach (string source in CurrentDataSources)
        {
            if (!Directory.Exists(source)) continue;
            try
            {
                FileSystemWatcher watcher = new(source)
                {
                    IncludeSubdirectories = true,
                    NotifyFilter = NotifyFilters.FileName | NotifyFilters.DirectoryName
                        | NotifyFilters.Size | NotifyFilters.LastWrite,
                };
                watcher.Changed += OnWatchedEntryChanged;
                watcher.Created += OnWatchedEntryChanged;
                watcher.Deleted += OnWatchedEntryChanged;
                watcher.Renamed += OnWatchedEntryRenamed;
                // A buffer overflow drops events; planning once more is enough to recover.
                watcher.Error += (_, _) => QueueDataRegionScan();
                watcher.EnableRaisingEvents = true;
                _dataSourceWatchers.Add(watcher);
            }
            catch (Exception exception) when (exception is IOException or UnauthorizedAccessException
                                                  or ArgumentException)
            {
                // An unwatchable folder still plans correctly; it just will not refresh itself.
            }
        }
    }

    private void OnWatchedEntryChanged(object? sender, FileSystemEventArgs e) => QueueDataRegionScan();

    private void OnWatchedEntryRenamed(object? sender, RenamedEventArgs e) => QueueDataRegionScan();

    /// <summary>Watcher callbacks run on a pool thread: hop to the UI thread before touching
    /// the debounce timer, skip while a job owns the shell, and skip once the project is
    /// closed — the watchers stay armed on the folders the last project pointed at.</summary>
    private void QueueDataRegionScan()
    {
        System.Windows.Application? application = System.Windows.Application.Current;
        if (application is null) return;
        application.Dispatcher.BeginInvoke(() =>
        {
            if (_shell.IsProjectOpen && !_shell.IsBusy) ScheduleDataRegion();
        });
    }
}
