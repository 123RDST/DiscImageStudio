using System.Collections.ObjectModel;
using System.IO;
using System.Text;
using System.Text.RegularExpressions;
using DiscImageStudio.Iso;
using DiscImageStudio.ViewModels;

/// <summary>Covers the signature file: content and encoding contract, the stamp-on-generate
/// behaviour, the recognition rule that never claims a user's own similarly-named pick, and
/// the project-load normalisation that re-points a saved entry from another machine.</summary>
internal static partial class AutoReadmeTests
{
    [GeneratedRegex("\u2014{4} \\d{4}-\\d{2}-\\d{2} \\d{2}:\\d{2}")]
    private static partial Regex StampLine();

    public static void Run()
    {
        if (File.Exists(DiscParametersState.AutoReadmePath)) File.Delete(DiscParametersState.AutoReadmePath);

        // Writing the file produces the agreed contract: UTF-8 BOM so every Windows notepad
        // renders the Chinese line, CRLF endings, the signature, and a minute timestamp.
        DiscParametersState.EnsureAutoReadmeFile();
        True(File.Exists(DiscParametersState.AutoReadmePath), "the signature file is written");
        byte[] raw = File.ReadAllBytes(DiscParametersState.AutoReadmePath);
        True(raw.Length >= 3 && raw[0] == 0xEF && raw[1] == 0xBB && raw[2] == 0xBF,
            "the signature file starts with a UTF-8 BOM");
        string text = Encoding.UTF8.GetString(raw);
        string content = text[1..]; // strip the BOM
        True(content.Contains("designed by 一只路过的小白虾\r\n", StringComparison.Ordinal),
            "the signature line is exact and CRLF-terminated");
        string[] lines = content.Split("\r\n", StringSplitOptions.RemoveEmptyEntries);
        Equal(2, lines.Length, "the file has two lines");
        True(StampLine().IsMatch(lines[1]), $"the stamp line matches: {lines[1]}");

        // Stamping only happens when the family's list carries the entry.
        string stampBefore = File.GetLastWriteTimeUtc(DiscParametersState.AutoReadmePath).Ticks.ToString();
        Thread.Sleep(20);
        DiscParametersState.RefreshAutoReadme([Path.Combine(Path.GetTempPath(), "unrelated.bin")]);
        Equal(stampBefore, File.GetLastWriteTimeUtc(DiscParametersState.AutoReadmePath).Ticks.ToString(),
            "a list without the entry does not touch the file");
        DiscParametersState.RefreshAutoReadme([DiscParametersState.AutoReadmePath]);
        string after = File.ReadAllText(DiscParametersState.AutoReadmePath);
        True(StampLine().IsMatch(after.Split("\r\n")[1]), "refreshing rewrites a valid stamp");

        // Recognition: the live path, a saved path from another machine's temp tree, and
        // nothing else — a user's own pick with the same leaf name is never ours to touch.
        True(DiscParametersState.IsAutoReadme(DiscParametersState.AutoReadmePath), "the live path is ours");
        True(DiscParametersState.IsAutoReadme(
                @"C:\Users\SomeoneElse\AppData\Local\Temp\DiscImageStudio\README.txt"),
            "a saved entry from another user's temp tree is ours");
        // A hand-written project may store forward slashes; the row still has to be ours.
        True(DiscParametersState.IsAutoReadme(
                "C:/Users/RDST/AppData/Local/Temp/DiscImageStudio/README.txt"),
            "a forward-slash temp path is ours");
        True(!DiscParametersState.IsAutoReadme(@"D:\Projects\DiscImageStudio\README.txt"),
            "a user pick outside the temp tree is not ours");
        True(!DiscParametersState.IsAutoReadme(null), "null is not ours");
        True(!DiscParametersState.IsAutoReadme(string.Empty), "an empty path is not ours");
        True(!DiscParametersState.IsAutoReadme(@"D:\readme.txt"), "a plain readme elsewhere is not ours");

        TestLoadNormalisation();
    }

    private static void TestLoadNormalisation()
    {
        ObservableCollection<string> sources = [];
        DiscParametersState.LoadSources(sources,
            [@"D:\My Files", @"C:\Users\Gone\AppData\Local\Temp\DiscImageStudio\README.txt"], null);
        Equal(2, sources.Count, "both saved entries load");
        Equal(DiscParametersState.AutoReadmePath, sources[1],
            "the stale signature path is re-pointed to the live one");
        True(File.Exists(DiscParametersState.AutoReadmePath),
            "a missing signature file is recreated on load");

        // The re-pointed entry plans like any file: one file, a handful of sectors.
        Iso9660ImageWriter.LayoutPlan plan = Iso9660ImageWriter.PlanSources(
            [DiscParametersState.AutoReadmePath], 2295104, "DISC_IMAGE");
        Equal(1, plan.FileCount, "the readme alone is one file");
        True(plan.DataEndLbaExclusive < 40,
            $"the readme-only filesystem stays tiny: {plan.DataEndLbaExclusive} sectors");
    }

    private static void Equal(int expected, int actual, string what)
    {
        if (expected != actual) throw new Exception($"{what}: expected {expected}, got {actual}");
    }

    private static void Equal(string expected, string actual, string what)
    {
        if (!string.Equals(expected, actual, StringComparison.Ordinal))
            throw new Exception($"{what}: expected {expected}, got {actual}");
    }

    private static void True(bool condition, string what)
    {
        if (!condition) throw new Exception(what);
    }
}
