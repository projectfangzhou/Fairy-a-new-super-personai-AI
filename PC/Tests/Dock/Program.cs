using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Shapes;
using MyAiAssistant.ViewModels;
using MyAiAssistant.Windows;

namespace Dock.Tests;

internal static class Program
{
    private static int _checks;

    [STAThread]
    private static int Main()
    {
        try
        {
            // 1. The rail catalog.
            var entries = DockCatalog.Entries;
            Assert(entries.Count == 6, "six dock entries");
            Assert(entries.Select(e => e.Action).Distinct().Count() == 6, "actions are unique");
            Assert(entries.Select(e => e.Title).Distinct().Count() == 6, "titles are unique");

            var enabled = entries.Where(e => e.IsEnabled).Select(e => e.Action).ToArray();
            Assert(enabled.Length == 3
                   && enabled.Contains(DockAction.ControlCenter)
                   && enabled.Contains(DockAction.Chat)
                   && enabled.Contains(DockAction.FairyCode),
                "only the implemented surfaces are clickable");
            Assert(entries.Where(e => !e.IsEnabled).All(e => !string.IsNullOrWhiteSpace(e.UnavailableReason)),
                "every disabled entry says why");

            // 2. Every icon is real stroked geometry, not an empty placeholder.
            foreach (var entry in entries)
            {
                var geometry = System.Windows.Media.Geometry.Parse(entry.IconData);
                Assert(!geometry.IsEmpty(), $"{entry.Title} icon parses");
                var bounds = geometry.Bounds;
                Assert(bounds.Width > 4 && bounds.Height > 4, $"{entry.Title} icon has drawable size");
            }

            // 3. Icon loading: file wins, missing or broken file falls back to the built-in outline.
            var iconDir = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "fairyai-dock-" + Guid.NewGuid().ToString("N"));
            System.IO.Directory.CreateDirectory(iconDir);
            try
            {
                var builtIn = DockCatalog.Find(DockAction.Chat).IconData;

                var missing = DockIconLoader.Load(iconDir, "chat", builtIn);
                Assert(!missing.FromFile && missing.Vector is not null, "missing artwork falls back to the built-in vector");

                System.IO.File.WriteAllText(System.IO.Path.Combine(iconDir, "chat.svg"), """
                    <svg xmlns="http://www.w3.org/2000/svg" viewBox="0 0 24 24">
                      <path d="M4 4 H20 V20 H4 Z"/>
                      <circle cx="12" cy="12" r="3"/>
                    </svg>
                    """);

                var fromSvg = DockIconLoader.Load(iconDir, "chat", builtIn);
                Assert(fromSvg.FromFile && fromSvg.Vector is not null, "svg artwork is picked up from disk");
                Assert(Math.Abs(fromSvg.Vector!.Bounds.Width - 16) < 0.01, "svg outline keeps its real coordinates");

                var broken = System.IO.Path.Combine(iconDir, "terminal.svg");
                System.IO.File.WriteAllText(broken, "<svg><path d=\"not a path\"/></svg>");
                var brokenIcon = DockIconLoader.Load(iconDir, "terminal", DockCatalog.Find(DockAction.Terminal).IconData);
                Assert(!brokenIcon.FromFile && brokenIcon.Vector is not null, "unreadable artwork falls back instead of crashing");

                Assert(DockIconLoader.ParseSvgOutline("<svg></svg>") is null, "artwork with no shapes yields nothing");
            }
            finally
            {
                try { System.IO.Directory.Delete(iconDir, true); } catch (System.IO.IOException) { }
            }

            // 4. Every shipped artwork file must load and sit inside the 24x24 frame with a
            //    sensible size — a path that silently fails to parse would look like a blank button.
            var assetsDir = FindAssetsDirectory();
            if (assetsDir is not null)
            {
                var required = DockCatalog.Entries.Select(e => e.IconKey).ToArray();
                foreach (var key in required)
                {
                    var file = System.IO.Path.Combine(assetsDir, key + ".svg");
                    Assert(System.IO.File.Exists(file), $"artwork delivered for {key}");

                    var icon = DockIconLoader.Load(assetsDir, key, DockCatalog.Find(EntryFor(key)).IconData);
                    Assert(icon.FromFile && icon.Vector is not null, $"{key} loads from its file");

                    var bounds = icon.Vector!.Bounds;
                    Assert(bounds.Left >= -0.01 && bounds.Top >= -0.01 &&
                           bounds.Right <= 24.01 && bounds.Bottom <= 24.01,
                        $"{key} stays inside the 24x24 frame");
                    Assert(bounds.Width >= 8 && bounds.Height >= 8,
                        $"{key} is drawn large enough to read ({bounds.Width:0.0}x{bounds.Height:0.0})");
                }
            }

            // 4b. Any other delivered artwork must be equally valid, so wiring it later cannot
            //     surprise us with a blank or oversized glyph.
            if (assetsDir is not null)
            {
                var all = System.IO.Directory.GetFiles(assetsDir, "*.svg")
                    .Where(f => !System.IO.Path.GetFileName(f).Equals("example.svg", StringComparison.OrdinalIgnoreCase))
                    .ToArray();
                Assert(all.Length >= 6, "artwork set delivered");

                foreach (var file in all)
                {
                    var key = System.IO.Path.GetFileNameWithoutExtension(file);
                    var icon = DockIconLoader.Load(assetsDir, key, "M0,0");
                    Assert(icon.FromFile && icon.Vector is not null, $"{key} parses");
                    var b = icon.Vector!.Bounds;
                    Assert(b.Left >= -0.01 && b.Top >= -0.01 && b.Right <= 24.01 && b.Bottom <= 24.01,
                        $"{key} fits the frame");
                    Assert(Math.Max(b.Width, b.Height) >= 13 && Math.Min(b.Width, b.Height) >= 4, $"{key} is not too small to read");
                }
            }

            // 5. The window wires buttons and routes clicks.
            var clicks = new List<DockAction>();
            var window = new DesktopDockWindow(clicks.Add);
            var host = window.FindName("DockHost") as StackPanel ?? throw new Exception("DockHost missing");
            var bottomHost = window.FindName("BottomHost") as StackPanel ?? throw new Exception("BottomHost missing");
            var buttons = host.Children.OfType<Button>().Concat(bottomHost.Children.OfType<Button>()).ToArray();
            Assert(host.Children.Count == 5, "five entries stack from the top");
            Assert(bottomHost.Children.Count == 1, "settings is pinned to the bottom");
            Assert(buttons.Count(b => b.IsEnabled) == 3, "three buttons are enabled");
            Assert(buttons.Count(b => !b.IsEnabled) == 3, "three buttons are disabled");

            var disabledWithTooltip = buttons.Where(b => !b.IsEnabled)
                .All(b => b.ToolTip is string tip && tip.Contains("计划中", StringComparison.Ordinal) ||
                          b.ToolTip is string other && other.Contains("控制中心", StringComparison.Ordinal));
            Assert(disabledWithTooltip, "disabled buttons explain themselves in a tooltip");

            var controlCenterButton = buttons[0];
            var chatButton = buttons[1];
            var terminalButton = buttons[2];
            var fairyCodeButton = buttons[3];

            controlCenterButton.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            chatButton.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            terminalButton.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            fairyCodeButton.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));

            Assert(clicks.Count == 3, "only enabled entries activate");
            Assert(clicks[0] == DockAction.ControlCenter
                   && clicks[1] == DockAction.Chat
                   && clicks[2] == DockAction.FairyCode,
                "clicks route to the right action");

            // 6. The rail hugs the right edge of the work area at full height.
            var area = SystemParameters.WorkArea;
            Assert(Math.Abs(window.Width - DesktopDockWindow.CollapsedWidth) < 0.01, "rail rests narrow so it cannot cover a close button");
            Assert(Math.Abs(window.Height - area.Height) < 0.01, "rail spans the work area height");
            Assert(Math.Abs(window.Left - (area.Right - DesktopDockWindow.CollapsedWidth)) < 0.01, "collapsed rail hugs the right edge");
            Assert(Math.Abs(window.Top - area.Top) < 0.01, "rail starts at the top of the work area");

            // 7. Hovering exposes the icons without moving the edge away from the screen.
            Assert(!window.IsExpanded, "rail starts collapsed");
            Assert(host.Visibility == Visibility.Collapsed, "icons hidden while collapsed");
            window.Expand();
            Assert(window.IsExpanded, "rail expands on demand");
            Assert(Math.Abs(window.Width - DesktopDockWindow.ExpandedWidth) < 0.01, "expanded rail is wider but still slim");
            Assert(Math.Abs(window.Left - (area.Right - DesktopDockWindow.ExpandedWidth)) < 0.01, "expanded rail keeps its right edge");
            Assert(host.Visibility == Visibility.Visible, "icons shown while expanded");
            window.Collapse();
            Assert(Math.Abs(window.Width - DesktopDockWindow.CollapsedWidth) < 0.01, "rail collapses again");
            Assert(host.Visibility == Visibility.Collapsed, "icons hidden again");

            window.Close();
            Console.WriteLine($"All {_checks} dock checks passed.");
            return 0;
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"FAILED: {ex.Message}");
            return 1;
        }
    }

    private static void Assert(bool condition, string name)
    {
        if (!condition) throw new Exception(name);
        _checks++;
    }

    private static DockAction EntryFor(string iconKey) =>
        DockCatalog.Entries.First(e => e.IconKey == iconKey).Action;

    private static string? FindAssetsDirectory()
    {
        var dir = new System.IO.DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null)
        {
            var candidate = System.IO.Path.Combine(dir.FullName, "Assets", "Dock");
            if (System.IO.Directory.Exists(candidate)) return candidate;
            dir = dir.Parent;
        }
        return null;
    }
}
