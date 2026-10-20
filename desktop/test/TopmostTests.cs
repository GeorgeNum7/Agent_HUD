using System;
using System.IO;
using System.Runtime.InteropServices;
using System.Threading;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Threading;

namespace AgentHud.Tests
{
    public static class TopmostTests
    {
        [DllImport("user32.dll")] static extern int GetWindowLong(IntPtr hwnd, int index);
        [DllImport("user32.dll")] static extern bool SetWindowPos(IntPtr hwnd, IntPtr after, int x, int y, int cx, int cy, uint flags);
        [DllImport("user32.dll")] static extern IntPtr GetForegroundWindow();
        [DllImport("user32.dll")] static extern IntPtr GetWindow(IntPtr hwnd, uint command);

        static bool NativePinned(Window window) { return (GetWindowLong(new WindowInteropHelper(window).Handle, -20) & 8) != 0; }
        static void Pump()
        {
            var frame = new DispatcherFrame();
            var timer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(200) };
            timer.Tick += delegate { timer.Stop(); frame.Continue = false; };
            timer.Start(); Dispatcher.PushFrame(frame);
        }
        static void Check(bool condition, string message)
        {
            if (!condition) throw new Exception(message);
            Console.WriteLine("PASS " + message);
        }
        static void Toggle(HudWindow hud, bool pinned)
        {
            var menu = ((FrameworkElement)hud.Content).ContextMenu;
            var item = (MenuItem)menu.Items[4];
            item.IsChecked = pinned;
            item.RaiseEvent(new RoutedEventArgs(MenuItem.ClickEvent));
            Pump();
        }
        static Button FindPinButton(DependencyObject node)
        {
            var button = node as Button;
            if (button != null && AutomationProperties.GetAutomationId(button) == "PinButton") return button;
            for (var i = 0; i < VisualTreeHelper.GetChildrenCount(node); i++)
            {
                var found = FindPinButton(VisualTreeHelper.GetChild(node, i));
                if (found != null) return found;
            }
            return null;
        }
        static void ClickPin(HudWindow hud)
        {
            var button = FindPinButton(hud);
            if (button == null) throw new Exception("pin button not found");
            button.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            Pump();
        }
        [STAThread]
        public static int Main(string[] args)
        {
            var baseline = args.Length > 0 && args[0] == "--baseline";
            var directory = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "state-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(directory);
            var app = new Application { ShutdownMode = ShutdownMode.OnExplicitShutdown };
            HudWindow hud = null;
            Window other = null;
            using (var signal = new EventWaitHandle(false, EventResetMode.AutoReset))
            using (var exitSignal = new EventWaitHandle(false, EventResetMode.AutoReset))
            {
                try
                {
                    BehaviorTests.Run();
                    hud = new HudWindow(directory, new Uri("http://127.0.0.1:1"), signal, false, exitSignal);
                    hud.Left = 100; hud.Top = 100;
                    hud.Show(); Pump();
                    var handle = new WindowInteropHelper(hud).Handle;
                    var owner = GetWindow(handle, 4);
                    Console.WriteLine("Initial: managed=" + hud.Topmost + ", native=" + NativePinned(hud) + ", hiddenOwnerTopmost=" + (owner != IntPtr.Zero && (GetWindowLong(owner, -20) & 8) != 0));
                    Check(NativePinned(hud), "pinned at startup");
                    BehaviorTests.CheckHiddenRefresh(hud);
                    hud.Hide(); signal.Set(); Pump(); Pump();
                    Check(hud.IsVisible, "show signal restores hidden widget without polling");
                    other = new Window { Title = "Agent HUD test window", Width = 280, Height = 70, Left = 100, Top = 100, ShowActivated = true };
                    other.Show(); other.Activate(); Pump();
                    Check(NativePinned(hud), "stays pinned when an ordinary window activates");
                    Toggle(hud, false);
                    Check(!NativePinned(hud) && !hud.Topmost, "unpin clears managed and native state");
                    hud.Hide(); Pump(); hud.Show(); Pump();
                    Check(!NativePinned(hud), "unpin survives hide/show");
                    Toggle(hud, true);
                    Check(NativePinned(hud) && hud.Topmost, "pin restores managed and native state");
                    ClickPin(hud);
                    Check(!NativePinned(hud) && !hud.Topmost, "pin button click unpins");
                    ClickPin(hud);
                    Check(NativePinned(hud) && hud.Topmost, "second pin button click pins again");
                    var setLanguage = typeof(HudWindow).GetMethod("SetLanguage", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance);
                    setLanguage.Invoke(hud, new object[] { "en" }); Pump();
                    var englishMenu = ((FrameworkElement)hud.Content).ContextMenu;
                    Check((string)((MenuItem)englishMenu.Items[4]).Header == "Always on top" && (string)((MenuItem)englishMenu.Items[7]).Header == "切换到中文" && AutomationProperties.GetName(FindPinButton(hud)).StartsWith("Pinned"), "switching to English rebuilds the menu and pin labels live");
                    Check(NativePinned(hud), "language switch keeps the window pinned");
                    setLanguage.Invoke(hud, new object[] { "zh" }); Pump();
                    Check((string)((MenuItem)((FrameworkElement)hud.Content).ContextMenu.Items[4]).Header == "始终置顶", "switching back restores Chinese");
                    var menu = ((FrameworkElement)hud.Content).ContextMenu;
                    menu.PlacementTarget = (UIElement)hud.Content;
                    menu.IsOpen = true; Pump(); menu.IsOpen = false; Pump();
                    Check(NativePinned(hud), "opening and closing the menu preserves pinning");
                    var foreground = GetForegroundWindow();
                    // Reproduce a native z-order change without changing WPF's cached Topmost value.
                    Check(SetWindowPos(handle, new IntPtr(-2), 0, 0, 0, 0, 0x213), "native demotion succeeded");
                    hud.Topmost = true;
                    Pump();
                    Console.WriteLine("After native demotion: managed=" + hud.Topmost + ", native=" + NativePinned(hud));
                    if (!baseline) Check(NativePinned(hud), "repairs native demotion while preference remains pinned");
                    Check(GetForegroundWindow() == foreground, "repair does not steal foreground focus");
                    hud.Hide(); Pump(); hud.Show(); Pump();
                    if (!baseline) Check(NativePinned(hud), "pinned hide/show restores native state");
                    SetWindowPos(handle, new IntPtr(-2), 0, 0, 0, 0, 0x213);
                    // Toggle before pumping the dispatcher: a previously queued repair must use the new preference.
                    Toggle(hud, false);
                    Pump();
                    Check(!NativePinned(hud), "queued recovery cannot override an explicit unpin");
                    hud.Close(); hud = null;
                    Pump();
                    hud = new HudWindow(directory, new Uri("http://127.0.0.1:1"), signal, false, exitSignal);
                    hud.Show(); Pump();
                    Check(!NativePinned(hud), "saved unpinned preference survives restart");
                    Toggle(hud, true);
                    hud.Close(); hud = null; Pump();
                    hud = new HudWindow(directory, new Uri("http://127.0.0.1:1"), signal, false, exitSignal);
                    hud.Show(); Pump();
                    Check(NativePinned(hud), "saved pinned preference survives restart");
                    var closed = false;
                    hud.Closed += delegate { closed = true; };
                    exitSignal.Set(); Pump(); Pump();
                    Check(closed, "update signal closes widget gracefully and saves state");
                    hud = null;
                    Console.WriteLine("Desktop topmost checks complete.");
                    return 0;
                }
                catch (Exception e) { Console.Error.WriteLine(e); return 1; }
                finally
                {
                    if (hud != null) hud.Close();
                    if (other != null) other.Close();
                    app.Shutdown();
                }
            }
        }
    }
}
