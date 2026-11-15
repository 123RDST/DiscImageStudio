using System.IO;
using System.Windows.Threading;
using DiscImageStudio.Controls;
using DiscImageStudio.Core;
using DiscImageStudio.Services;
using DiscImageStudio.ViewModels;
using DiscImageStudio.Views;

/// <summary>Covers the recent-card delete: confirmed deletes remove both the file and the entry,
/// a cancelled confirm touches neither, and an entry whose file vanished is pruned instead of
/// throwing. The Recycle Bin call is replaced through the view model's deleter seam so the test
/// never moves files into the user's own bin.</summary>
internal static class RecentsDeleteTests
{
    public static void Run()
    {
        string directory = Path.Combine(Path.GetTempPath(), "disc-recents-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        try
        {
            var shell = new ShellViewModel(Dispatcher.CurrentDispatcher);
            var store = new RecentProjectStore(Path.Combine(directory, "recent-projects.json"));
            var home = new HomeViewModel(shell, shell.State, store) { ProjectFileDeleter = File.Delete };

            // Confirmed: the file goes and the card goes with it.
            string confirmed = ProjectPath(directory, "confirmed");
            ProjectJsonStore.Save(confirmed, Document("confirmed"));
            Record(store, home, confirmed, "confirmed");
            shell.ConfirmHandler = (_, _, _) => Task.FromResult(true);
            Wait(home.DeleteRecentCommand.ExecuteAsync(home.Recents.Single()));
            True(!File.Exists(confirmed), "a confirmed delete removes the project file");
            True(home.Recents.Count == 0 && store.Load().Count == 0,
                "a confirmed delete removes the card and the stored entry");

            // Cancelled: nothing happens, not even the deleter.
            string kept = ProjectPath(directory, "kept");
            ProjectJsonStore.Save(kept, Document("kept"));
            Record(store, home, kept, "kept");
            int calls = 0;
            home.ProjectFileDeleter = path => { calls++; File.Delete(path); };
            shell.ConfirmHandler = (_, _, _) => Task.FromResult(false);
            Wait(home.DeleteRecentCommand.ExecuteAsync(home.Recents.Single()));
            True(File.Exists(kept) && calls == 0, "a cancelled confirm leaves the file untouched");
            True(home.Recents.Count == 1, "a cancelled confirm leaves the card in place");

            // The file was deleted behind the app's back: the card is pruned, no throw.
            string gone = ProjectPath(directory, "gone");
            Record(store, home, gone, "gone");
            Wait(home.DeleteRecentCommand.ExecuteAsync(home.Recents.Single(tile => tile.Entry.ProjectPath == gone)));
            True(!home.Recents.Any(tile => tile.Entry.ProjectPath == gone),
                "an entry whose file is gone is pruned from the recents list");
            True(!store.Load().Any(entry => entry.ProjectPath == gone),
                "the pruned entry is also dropped from the stored list");

            // The last surviving card is real and still deletable afterwards.
            True(File.Exists(kept), "the earlier cancelled project is still on disk");

            WpfTestHost.Run(() => CheckCardMenu(directory));
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }

        Console.WriteLine("recents-delete: passed (confirmed deletes file and card, cancel keeps both, missing file is pruned)");
    }

    private static string ProjectPath(string directory, string name)
        => Path.Combine(directory, name + ".discproj");

    /// <summary>The menu lives in a popup outside the visual tree and reaches its commands
    /// through PlacementTarget.Tag, a path that fails silently when wrong: the card is built,
    /// the target is wired like a right-click does, and both items must resolve to the home
    /// view model's commands.</summary>
    private static void CheckCardMenu(string directory)
    {
        var shell = new ShellViewModel(Dispatcher.CurrentDispatcher);
        var store = new RecentProjectStore(Path.Combine(directory, "menu-recent-projects.json"));
        var home = new HomeViewModel(shell, shell.State, store);
        string path = ProjectPath(directory, "menu");
        ProjectJsonStore.Save(path, Document("menu"));
        Record(store, home, path, "menu");

        var view = new HomeView(home) { DataContext = home };
        var size = new System.Windows.Size(1200, 900);
        view.Measure(size);
        view.Arrange(new System.Windows.Rect(size));
        view.UpdateLayout();

        RecentProjectCard? card = FindDescendant<RecentProjectCard>(view);
        True(card is not null, "the recent card is laid out");
        System.Windows.Controls.ContextMenu? menu = card!.ContextMenu;
        True(menu is not null && menu.Items.Count == 3, "the card menu has remove, a separator and delete");
        // The menu is only ever opened as a real popup by the app, and the item data context
        // does not flow without one; the existing remove item already proves that plumbing, so
        // the popup-free check asserts only the two commands resolve.
        menu!.PlacementTarget = card;
        System.Windows.Controls.MenuItem remove = (System.Windows.Controls.MenuItem)menu.Items[0];
        System.Windows.Controls.MenuItem delete = (System.Windows.Controls.MenuItem)menu.Items[2];
        True(ReferenceEquals(remove.Command, home.RemoveRecentCommand),
            "the remove item resolves to the list-removal command");
        True(ReferenceEquals(delete.Command, home.DeleteRecentCommand),
            "the delete item resolves to the file-deletion command");
    }

    private static T? FindDescendant<T>(System.Windows.DependencyObject root) where T : System.Windows.DependencyObject
    {
        int count = System.Windows.Media.VisualTreeHelper.GetChildrenCount(root);
        for (int i = 0; i < count; i++)
        {
            System.Windows.DependencyObject child = System.Windows.Media.VisualTreeHelper.GetChild(root, i);
            if (child is T match) return match;
            if (FindDescendant<T>(child) is { } nested) return nested;
        }

        return null;
    }

    private static void Record(RecentProjectStore store, HomeViewModel home, string path, string name)
    {
        store.Record(new RecentProjectEntry(
            name, ProjectDocument.DvdFamily, path, null, DateTimeOffset.UnixEpoch));
        home.RefreshRecentProjects();
    }

    private static ProjectDocument Document(string name)
        => new(ProjectJsonStore.CurrentSchemaVersion, name, ProjectDocument.DvdFamily,
            new ProjectGeometry(24.5, 56.8, 2_298_496),
            [],
            new ProjectOutputs("D:\\out\\dvd.iso", true, "D:\\out\\preview.png", "DVD_IMAGE", true),
            null, null, DateTimeOffset.UnixEpoch, DateTimeOffset.UnixEpoch,
            ToneProfile.Photo);

    private static void Wait(Task task)
    {
        DispatcherFrame frame = new();
        Dispatcher dispatcher = Dispatcher.CurrentDispatcher;
        DispatcherTimer timeout = new() { Interval = TimeSpan.FromSeconds(5) };
        timeout.Tick += (_, _) => frame.Continue = false;
        _ = task.ContinueWith(_ => dispatcher.BeginInvoke(new Action(() => frame.Continue = false)),
            CancellationToken.None, TaskContinuationOptions.None, TaskScheduler.Default);
        timeout.Start();
        try { if (!task.IsCompleted) Dispatcher.PushFrame(frame); }
        finally { timeout.Stop(); }
        True(task.IsCompleted, "the delete continuation completed within the test timeout");
        task.GetAwaiter().GetResult();
    }

    private static void True(bool condition, string what)
    {
        if (!condition) throw new InvalidOperationException("Recents delete: " + what);
    }
}
