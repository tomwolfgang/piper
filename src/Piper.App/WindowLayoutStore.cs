using System.Drawing;
using System.Text.Json;
using Piper.App.Layout;
using Piper.Core.Sessions;
using Piper.Core.Text;

namespace Piper.App;

/// <summary>
/// Stores Piper's last normal window bounds and panel layout in local app data. Layout is convenience
/// state and the file is hostile input, so everything read is bounded and unusable parts fall back to
/// defaults; nothing here may prevent the application from opening. The store knows nothing about
/// screens: the caller runs <see cref="PanelLayout.Normalise"/> on the loaded panels.
/// </summary>
internal static class WindowLayoutStore
{
    public const int MaxFileBytes = 65536;
    private const int CurrentVersion = 2;
    private const int MaxJsonDepth = 8;

    /// <summary>Bounds, in 96-DPI pixels at 100% zoom, for a session-list column width read back from disk.</summary>
    public const int MinColumnWidth = 20;
    public const int MaxColumnWidth = 2000;

    private static readonly PanelId[] AllPanels = Enum.GetValues<PanelId>();
    private static readonly DockArea[] AllAreas = Enum.GetValues<DockArea>();
    private static readonly SessionSortColumn[] AllColumns = Enum.GetValues<SessionSortColumn>();

    public static string DefaultPath => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Piper", "window-layout.json");

    public static void Save(WindowLayout layout, string? path = null)
    {
        path ??= DefaultPath;
        string? temp = null;
        try
        {
            var directory = Path.GetDirectoryName(path);
            if (!string.IsNullOrEmpty(directory)) Directory.CreateDirectory(directory);
            temp = $"{path}.{Guid.NewGuid():N}.tmp";
            using (var stream = new FileStream(temp, FileMode.CreateNew, FileAccess.Write, FileShare.None))
            using (var writer = new Utf8JsonWriter(stream))
                Write(writer, layout);
            File.Move(temp, path, overwrite: true);
            temp = null;
        }
        catch (IOException)
        {
        }
        catch (UnauthorizedAccessException)
        {
        }
        finally
        {
            if (temp is not null) TryDelete(temp);
        }
    }

    /// <summary>The saved layout, or null when there is no file or it has no usable window bounds.</summary>
    public static WindowLayout? Load(string? path = null)
    {
        path ??= DefaultPath;
        try
        {
            using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
            if (!BoundedRead.TryRead(stream, MaxFileBytes, out var content, out var length)) return null;
            using var document = JsonDocument.Parse(content.AsMemory(0, length), new JsonDocumentOptions { MaxDepth = MaxJsonDepth });
            return Parse(document.RootElement);
        }
        catch (IOException)
        {
            return null;
        }
        catch (JsonException)
        {
            return null;
        }
        catch (UnauthorizedAccessException)
        {
            return null;
        }
    }

    private static void TryDelete(string file)
    {
        try
        {
            File.Delete(file);
        }
        catch (IOException)
        {
        }
        catch (UnauthorizedAccessException)
        {
        }
    }

    // ---- writing ---------------------------------------------------------------------------

    private static void Write(Utf8JsonWriter writer, WindowLayout layout)
    {
        var panels = layout.Panels;
        writer.WriteStartObject();
        writer.WriteNumber("Version", CurrentVersion);
        writer.WriteNumber("X", layout.X);
        writer.WriteNumber("Y", layout.Y);
        writer.WriteNumber("Width", layout.Width);
        writer.WriteNumber("Height", layout.Height);
        writer.WriteBoolean("Maximized", layout.Maximized);
        writer.WriteBoolean("InspectorSideBySide", layout.InspectorSideBySide);
        writer.WriteNumber("InspectorSplit", layout.InspectorSplit);

        writer.WriteStartArray("Panels");
        foreach (var id in AllPanels)
        {
            var area = panels.AreaOf(id);
            // A hidden panel is in no tab list, but it remembers the place it will be shown again at.
            var order = panels.IsHidden(id) ? panels.OrderOf(id) : panels.DockedIn(area).ToList().IndexOf(id);
            writer.WriteStartObject();
            writer.WriteString("Id", Name(id));
            writer.WriteString("Area", Name(area));
            writer.WriteNumber("Order", Math.Max(order, 0));
            writer.WriteBoolean("Hidden", panels.IsHidden(id));
            writer.WriteBoolean("Selected", panels.SelectedIn(area) == id);
            if (panels.FloatingBounds(id) is { } bounds)
            {
                writer.WriteStartObject("Floating");
                writer.WriteNumber("X", bounds.X);
                writer.WriteNumber("Y", bounds.Y);
                writer.WriteNumber("Width", bounds.Width);
                writer.WriteNumber("Height", bounds.Height);
                writer.WriteNumber("Dpi", panels.FloatingDpi(id));
                writer.WriteBoolean("TopMost", panels.TopMost(id));
                writer.WriteEndObject();
            }
            else
            {
                writer.WriteNull("Floating");
            }

            writer.WriteEndObject();
        }

        writer.WriteEndArray();

        writer.WriteStartObject("Areas");
        writer.WriteNumber("LeftFraction", panels.LeftFraction);
        writer.WriteNumber("BottomFraction", panels.BottomFraction);
        foreach (var area in AllAreas)
        {
            writer.WriteStartObject(Name(area));
            writer.WriteBoolean("AutoHidden", panels.IsAutoHidden(area));
            writer.WriteNumber("Flyout", panels.FlyoutFraction(area));
            writer.WriteEndObject();
        }

        writer.WriteEndObject();

        // Only the columns the user sized; the rest keep sharing the grid's width.
        writer.WriteStartObject("SessionColumns");
        foreach (var (column, width) in layout.SessionColumnWidths)
            writer.WriteNumber(column.ToString().ToLowerInvariant(), Math.Clamp(width, MinColumnWidth, MaxColumnWidth));
        writer.WriteEndObject();

        writer.WriteEndObject();
        writer.Flush();
    }

    private static string Name(PanelId id) => id.ToString().ToLowerInvariant();

    private static string Name(DockArea area) => area.ToString().ToLowerInvariant();

    // ---- reading ---------------------------------------------------------------------------

    private static WindowLayout? Parse(JsonElement root)
    {
        if (root.ValueKind != JsonValueKind.Object) return null;
        if (!TryInt(root, "X", out var x) || !TryInt(root, "Y", out var y)
            || !TryInt(root, "Width", out var width) || !TryInt(root, "Height", out var height)
            || width <= 0 || height <= 0)
            return null;

        var maximized = root.TryGetProperty("Maximized", out var m) && m.ValueKind == JsonValueKind.True;
        var panels = PanelLayout.Default();
        if (root.TryGetProperty("Panels", out var list) && list.ValueKind == JsonValueKind.Array) ReadPanels(list, panels);
        if (root.TryGetProperty("Areas", out var areas) && areas.ValueKind == JsonValueKind.Object) ReadAreas(areas, panels);

        var columns = new Dictionary<SessionSortColumn, int>();
        if (root.TryGetProperty("SessionColumns", out var widths) && widths.ValueKind == JsonValueKind.Object)
        {
            // Looked up by known column only, so unknown names are ignored and the count is bounded.
            foreach (var column in AllColumns)
                if (TryInt(widths, column.ToString().ToLowerInvariant(), out var w))
                    columns[column] = Math.Clamp(w, MinColumnWidth, MaxColumnWidth);
        }

        // Anything but a literal true keeps the stacked default.
        var sideBySide = root.TryGetProperty("InspectorSideBySide", out var s) && s.ValueKind == JsonValueKind.True;
        return new WindowLayout(new Rectangle(x, y, width, height), maximized, panels)
        {
            SessionColumnWidths = columns,
            InspectorSideBySide = sideBySide,
            InspectorSplit = TryDouble(root, "InspectorSplit", out var split) ? split : 0.5,
        };
    }

    private static void ReadPanels(JsonElement list, PanelLayout panels)
    {
        var seen = new HashSet<PanelId>();
        foreach (var item in list.EnumerateArray())
        {
            // The id comes first so a bad entry for a known panel falls back to that panel's default.
            if (item.ValueKind != JsonValueKind.Object || !TryName(item, "Id", AllPanels, out var id) || !seen.Add(id)) continue;
            try
            {
                if (!TryName(item, "Area", AllAreas, out var area)) continue;
                var order = TryInt(item, "Order", out var o) ? o : 0;
                var hidden = item.TryGetProperty("Hidden", out var h) && h.ValueKind == JsonValueKind.True;
                var selected = item.TryGetProperty("Selected", out var s) && s.ValueKind == JsonValueKind.True;

                Rectangle? floating = null;
                var dpi = 96;
                var topMost = false;
                if (item.TryGetProperty("Floating", out var f) && f.ValueKind != JsonValueKind.Null)
                {
                    if (f.ValueKind != JsonValueKind.Object
                        || !TryInt(f, "X", out var fx) || !TryInt(f, "Y", out var fy)
                        || !TryInt(f, "Width", out var fw) || !TryInt(f, "Height", out var fh))
                        continue;
                    floating = new Rectangle(fx, fy, fw, fh);
                    dpi = TryInt(f, "Dpi", out var d) ? d : 96;
                    topMost = f.TryGetProperty("TopMost", out var t) && t.ValueKind == JsonValueKind.True;
                }

                panels.Restore(id, area, order, hidden, selected, floating, dpi, topMost);
            }
            catch (InvalidOperationException)
            {
                // A malformed entry leaves this panel at its default placement.
            }
        }
    }

    private static void ReadAreas(JsonElement areas, PanelLayout panels)
    {
        if (TryDouble(areas, "LeftFraction", out var left)) panels.LeftFraction = left;
        if (TryDouble(areas, "BottomFraction", out var bottom)) panels.BottomFraction = bottom;
        foreach (var area in AllAreas)
        {
            try
            {
                if (!areas.TryGetProperty(Name(area), out var state) || state.ValueKind != JsonValueKind.Object) continue;
                if (TryDouble(state, "Flyout", out var flyout)) panels.SetFlyoutFraction(area, flyout);
                if (state.TryGetProperty("AutoHidden", out var a) && a.ValueKind == JsonValueKind.True) panels.SetAutoHidden(area, true);
            }
            catch (InvalidOperationException)
            {
            }
        }
    }

    private static bool TryName<T>(JsonElement element, string property, T[] values, out T result) where T : struct, Enum
    {
        result = default;
        if (!element.TryGetProperty(property, out var value) || value.ValueKind != JsonValueKind.String) return false;
        var text = value.GetString();
        foreach (var candidate in values)
        {
            if (!string.Equals(candidate.ToString(), text, StringComparison.OrdinalIgnoreCase)) continue;
            result = candidate;
            return true;
        }

        return false;
    }

    private static bool TryInt(JsonElement element, string property, out int result)
    {
        result = 0;
        return element.TryGetProperty(property, out var value) && value.ValueKind == JsonValueKind.Number && value.TryGetInt32(out result);
    }

    private static bool TryDouble(JsonElement element, string property, out double result)
    {
        result = 0;
        return element.TryGetProperty(property, out var value) && value.ValueKind == JsonValueKind.Number && value.TryGetDouble(out result);
    }

    internal sealed class WindowLayout
    {
        public WindowLayout()
        {
        }

        public WindowLayout(Rectangle bounds, bool maximized, PanelLayout panels)
        {
            X = bounds.X;
            Y = bounds.Y;
            Width = bounds.Width;
            Height = bounds.Height;
            Maximized = maximized;
            Panels = panels;
        }

        public int Version { get; } = CurrentVersion;
        public int X { get; set; }
        public int Y { get; set; }
        public int Width { get; set; }
        public int Height { get; set; }
        public bool Maximized { get; set; }
        public bool InspectorSideBySide { get; set; }

        /// <summary>The request/response splitter as a fraction of the inspector's length, clamped to 0.1-0.9.</summary>
        public double InspectorSplit
        {
            get;
            set => field = PanelLayout.ClampFraction(value, 0.5);
        } = 0.5;

        public PanelLayout Panels { get; set; } = PanelLayout.Default();

        /// <summary>Session-list columns the user resized, in 96-DPI pixels at 100% zoom.</summary>
        public IReadOnlyDictionary<SessionSortColumn, int> SessionColumnWidths { get; set; } = new Dictionary<SessionSortColumn, int>();

        public Rectangle ToRectangle() => new(X, Y, Width, Height);
    }
}
