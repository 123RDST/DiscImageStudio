using System.Collections.ObjectModel;
using System.IO;
using System.Text;
using CommunityToolkit.Mvvm.ComponentModel;

namespace DiscImageStudio.ViewModels;

/// <summary>
/// The files that travel with a disc. A disc used to take a single folder; it now takes a
/// list of picks, so one disc can carry several folders plus loose files. The old single-path
/// properties remain as views over these lists: reading returns the first source and assigning
/// replaces the whole list, which keeps saved projects, presets and the burn/export pipelines
/// working unchanged while the design page shows the list.
/// </summary>
public partial class DiscParametersState
{
    /// <summary>Folders and files that go onto the CD, in the order they were added.</summary>
    public ObservableCollection<string> CdDataSources { get; } = [];

    /// <summary>Folders and files that go onto the DVD, in the order they were added.</summary>
    public ObservableCollection<string> DvdDataSources { get; } = [];

    /// <summary>Folders and files that go onto the Blu-ray, in the order they were added.</summary>
    public ObservableCollection<string> BdDataSources { get; } = [];

    /// <summary>First source, or empty string. Assigning replaces the list with one entry.</summary>
    public string CdDataDirectory
    {
        get => FirstSource(CdDataSources);
        set => ReplaceSources(CdDataSources, value);
    }

    /// <summary>First source, or empty string. Assigning replaces the list with one entry.</summary>
    public string DvdDataDirectory
    {
        get => FirstSource(DvdDataSources);
        set => ReplaceSources(DvdDataSources, value);
    }

    /// <summary>First source, or empty string. Assigning replaces the list with one entry.</summary>
    public string BdDataDirectory
    {
        get => FirstSource(BdDataSources);
        set => ReplaceSources(BdDataSources, value);
    }

    /// <summary>Every source joined into one key for job identity and cache keys, so adding a
    /// second folder invalidates work that was built from the first one alone.</summary>
    public string CdSourcesKey => SourcesKey(CdDataSources);

    /// <inheritdoc cref="CdSourcesKey"/>
    public string DvdSourcesKey => SourcesKey(DvdDataSources);

    /// <inheritdoc cref="CdSourcesKey"/>
    public string BdSourcesKey => SourcesKey(BdDataSources);

    /// <summary>The signature file the design page can drop into a family's list. A stable
    /// path (not a fresh temp name per burn) is what lets the entry be saved with the project,
    /// reordered like any other source, and restamped before each generation.</summary>
    public static readonly string AutoReadmePath =
        Path.Combine(Path.GetTempPath(), "DiscImageStudio", "README.txt");

    /// <summary>Recognises the signature entry: the current temp path, or one saved into a
    /// project on another machine or user (still under a Windows temp folder). A merely
    /// similar name elsewhere on disk is the user's own pick and never ours to touch.
    /// Separators are normalised first: a hand-written project may store forward slashes.</summary>
    public static bool IsAutoReadme(string? path)
    {
        if (string.IsNullOrWhiteSpace(path)) return false;
        string normalized = path.Trim().Replace('/', '\\');
        if (string.Equals(normalized, AutoReadmePath, StringComparison.OrdinalIgnoreCase)) return true;
        // Pattern, not leaf-name match: "\AppData\Local\Temp\DiscImageStudio\README.txt"
        // cannot collide with a folder the user picked outside the temp tree.
        return normalized.EndsWith(@"\AppData\Local\Temp\DiscImageStudio\README.txt",
            StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>A saved entry may carry another machine's temp prefix; the burn pipelines
    /// compare against the live <see cref="AutoReadmePath"/>, so re-point on load.</summary>
    private static void NormalizeAutoReadme(ObservableCollection<string> sources)
    {
        for (int i = 0; i < sources.Count; i++)
        {
            if (IsAutoReadme(sources[i]) && !string.Equals(sources[i], AutoReadmePath,
                    StringComparison.OrdinalIgnoreCase))
            {
                sources[i] = AutoReadmePath;
            }
        }

        // The temp folder may have been cleaned since the project was saved; the signature
        // file is ours to recreate, so a missing one never breaks the load.
        if (sources.Contains(AutoReadmePath) && !File.Exists(AutoReadmePath)) EnsureAutoReadmeFile();
    }

    /// <summary>Writes the signature file: UTF-8 with BOM and CRLF so every Windows notepad
    /// renders the Chinese line, with the timestamp of the moment of writing.</summary>
    public static void EnsureAutoReadmeFile()
    {
        string directory = Path.GetDirectoryName(AutoReadmePath)!;
        Directory.CreateDirectory(directory);
        string content =
            "designed by 一只路过的小白虾\r\n"
            + $"\u2014\u2014\u2014\u2014 {DateTime.Now:yyyy-MM-dd HH:mm}\r\n";
        File.WriteAllText(AutoReadmePath, content, new UTF8Encoding(encoderShouldEmitUTF8Identifier: true));
    }

    /// <summary>Restamps the signature file at the moment a disc is burned or exported, so the
    /// file's content and its recorded timestamp say when that disc was made. A no-op for
    /// families whose list does not carry it.</summary>
    public static void RefreshAutoReadme(IReadOnlyList<string> sources)
    {
        if (sources.Any(IsAutoReadme)) EnsureAutoReadmeFile();
    }

    /// <summary>Adds a folder or file unless it is already listed. Blank paths and duplicates
    /// return false so the caller can leave the list alone without checking first.</summary>
    public static bool AddSource(ObservableCollection<string> sources, string? path)
    {
        ArgumentNullException.ThrowIfNull(sources);
        if (string.IsNullOrWhiteSpace(path)) return false;
        string full = Path.GetFullPath(path.Trim());
        if (sources.Any(source => string.Equals(source, full, StringComparison.OrdinalIgnoreCase)))
            return false;
        sources.Add(full);
        return true;
    }

    /// <summary>Restores a saved project's sources: the list wins when present, and files that
    /// only stored one folder fall back to it. Blank entries are dropped.</summary>
    public static void LoadSources(ObservableCollection<string> sources, IReadOnlyList<string>? list,
        string? singleDirectory)
    {
        ArgumentNullException.ThrowIfNull(sources);
        sources.Clear();
        if (list is { Count: > 0 })
        {
            foreach (string item in list)
            {
                if (!string.IsNullOrWhiteSpace(item)) sources.Add(item);
            }

            NormalizeAutoReadme(sources);
            return;
        }

        if (!string.IsNullOrWhiteSpace(singleDirectory)) sources.Add(singleDirectory.Trim());
    }

    /// <summary>Wires the change notifications the old generated properties used to raise.
    /// Called once from the constructor.</summary>
    private void AttachDataSources()
    {
        CdDataSources.CollectionChanged += (_, _) => NotifyCdSourcesChanged();
        DvdDataSources.CollectionChanged += (_, _) => NotifyDvdSourcesChanged();
        BdDataSources.CollectionChanged += (_, _) => NotifyBdSourcesChanged();
    }

    private void NotifyCdSourcesChanged()
    {
        OnPropertyChanged(nameof(CdDataDirectory));
        OnPropertyChanged(nameof(HasCdFiles));
        OnPropertyChanged(nameof(IsCdAudioMode));
        OnPropertyChanged(nameof(IsCdWaveOutput));
        OnPropertyChanged(nameof(CdOutputLabel));
        OnPropertyChanged(nameof(CdOutputHint));
        OnPropertyChanged(nameof(UsesCdVolumeLabel));
        // The data folder decides whether the CD exports an ISO or audio tracks.
        NormalizeCdOutputFormat();
    }

    private void NotifyDvdSourcesChanged()
    {
        OnPropertyChanged(nameof(DvdDataDirectory));
        OnPropertyChanged(nameof(HasDvdFiles));
    }

    private void NotifyBdSourcesChanged()
    {
        OnPropertyChanged(nameof(BdDataDirectory));
        OnPropertyChanged(nameof(HasBluRayFiles));
    }

    private static string FirstSource(ObservableCollection<string> sources)
        => sources.Count == 0 ? string.Empty : sources[0];

    private static string SourcesKey(ObservableCollection<string> sources)
        => string.Join("|", sources);

    private static void ReplaceSources(ObservableCollection<string> sources, string? value)
    {
        string trimmed = value?.Trim() ?? string.Empty;
        if (sources.Count <= 1
            && string.Equals(FirstSource(sources), trimmed, StringComparison.Ordinal))
        {
            return;
        }

        sources.Clear();
        if (trimmed.Length > 0) sources.Add(trimmed);
    }
}
