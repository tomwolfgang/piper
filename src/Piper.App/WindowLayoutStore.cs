using System.Drawing;
using System.Text.Json;

namespace Piper.App;

/// <summary>
/// Stores Piper's last normal window bounds in local app data. Layout is convenience state, so
/// unreadable data is ignored and must never prevent the application from opening.
/// </summary>
internal static class WindowLayoutStore
{
    public static string DefaultPath => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Piper", "window-layout.json");

    public static void Save(Rectangle bounds, bool maximized, string? path = null)
    {
        path ??= DefaultPath;
        try
        {
            var directory = Path.GetDirectoryName(path);
            if (!string.IsNullOrEmpty(directory)) Directory.CreateDirectory(directory);
            File.WriteAllText(path, JsonSerializer.Serialize(new WindowLayout(bounds, maximized)));
        }
        catch (IOException)
        {
        }
        catch (UnauthorizedAccessException)
        {
        }
    }

    public static WindowLayout? Load(string? path = null)
    {
        path ??= DefaultPath;
        try
        {
            if (!File.Exists(path)) return null;
            return JsonSerializer.Deserialize<WindowLayout>(File.ReadAllText(path));
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

    internal sealed class WindowLayout
    {
        public WindowLayout()
        {
        }

        public WindowLayout(Rectangle bounds, bool maximized)
        {
            X = bounds.X;
            Y = bounds.Y;
            Width = bounds.Width;
            Height = bounds.Height;
            Maximized = maximized;
        }

        public int X { get; set; }
        public int Y { get; set; }
        public int Width { get; set; }
        public int Height { get; set; }
        public bool Maximized { get; set; }

        public Rectangle ToRectangle() => new(X, Y, Width, Height);
    }
}
