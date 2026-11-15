using System.IO;
using System.Text.Json;
using DiscImageStudio.Core;
using DiscImageStudio.Core.Calibration;
using DiscImageStudio.Dvd;
using DiscImageStudio.ImageIO;

/// <summary>The exported ISO carries the geometry it was generated with, angular correction
/// included. A missing table is not a cosmetic detail: the pattern lands at the wrong angle for
/// its radius, so a straight radial line comes out bent near the outer edge — which is exactly
/// what happened when a project opened without the measured table overwrote the loaded one.</summary>
internal static class ExportAngularBakeTests
{
    internal static void Run()
    {
        string directory = Path.Combine(Path.GetTempPath(), $"dvd-angular-bake-{Guid.NewGuid():N}");
        Directory.CreateDirectory(directory);
        try
        {
            string source = Path.Combine(directory, "radial-line.png");
            RasterCodec.SavePng(source, 256, 256, RadialLine(256), grayscale: false);

            AngularResidualTable table = AngularResidualTable.Create(
            [
                new AngularResidualPoint(0.0, 0.0),
                new AngularResidualPoint(0.5, 0.010),
                new AngularResidualPoint(1.0, 0.024),
            ]);

            byte[] plain = Generate(source, Path.Combine(directory, "plain.iso"), table: null);
            byte[] corrected = Generate(source, Path.Combine(directory, "corrected.iso"), table);

            Require(!plain.AsSpan().SequenceEqual(corrected),
                "the angular table changes the generated sectors, so it is baked into the file");
            Require(SidecarAngular(directory, "plain.iso") is null, "a generation without a table records none");
            // The sidecar stores the table as {"points":[{x,radians},…]}: the point count is what
            // the user reads when checking which correction a file was built with.
            Require(SidecarAngular(directory, "corrected.iso") is { } recorded
                && recorded.GetProperty("points").GetArrayLength() == 3,
                "a generation with a table records all of its points");
        }
        finally
        {
            try { Directory.Delete(directory, recursive: true); } catch (IOException) { }
        }
    }

    private static byte[] Generate(string source, string iso, AngularResidualTable? table)
    {
        DvdStreamingOptions options = new(
            TotalSectors: 256,
            InnerRadiusMm: 24,
            OuterRadiusMm: 58,
            FastParallelism: 2,
            AngularResidual: table);
        DvdImageGenerator.Generate(new DvdImageRequest(source, iso, options));
        return File.ReadAllBytes(iso);
    }

    private static JsonElement? SidecarAngular(string directory, string name)
    {
        using JsonDocument metadata = JsonDocument.Parse(
            File.ReadAllText(Path.Combine(directory, name + ".json")));
        JsonElement value = metadata.RootElement
            .GetProperty("imageMapping")
            .GetProperty("angularResidual");
        return value.ValueKind is JsonValueKind.Null ? null : value.Clone();
    }

    /// <summary>A single dark vertical line through the centre on a white field: the pattern the
    /// user reads as "is the line straight?".</summary>
    private static byte[] RadialLine(int size)
    {
        byte[] pixels = new byte[size * size * 4];
        int centre = size / 2;
        for (int y = 0; y < size; y++)
        {
            for (int x = 0; x < size; x++)
            {
                int offset = (y * size + x) * 4;
                byte value = Math.Abs(x - centre) <= 1 ? (byte)0 : (byte)255;
                pixels[offset] = pixels[offset + 1] = pixels[offset + 2] = value;
                pixels[offset + 3] = 255;
            }
        }
        return pixels;
    }

    private static void Require(bool condition, string message)
    {
        if (!condition) throw new InvalidOperationException("export-angular: " + message);
    }
}
