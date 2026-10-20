using System;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Threading;

namespace AgentHud
{
    // WPF's Topmost property can remain true after the native HWND is demoted.
    // Reconcile the actual flag after window lifecycle / z-order changes. Do not
    // repeatedly raise an already-topmost window or activate it over another app.
    public sealed class WindowPin : IDisposable
    {
        readonly Window window;
        readonly Action<int> reportError;
        HwndSource source;
        bool pinned, pending, disposed, applying;
        int lastError;

        const int ExStyle = -20, WsExTopmost = 8;
        const uint PositionFlags = 0x0001 | 0x0002 | 0x0010 | 0x0200; // no size, move, activation or owner reordering
        [DllImport("user32.dll", EntryPoint = "GetWindowLongW")] static extern int GetWindowLong(IntPtr hwnd, int index);
        [DllImport("user32.dll", SetLastError = true)] static extern bool SetWindowPos(IntPtr hwnd, IntPtr after, int x, int y, int cx, int cy, uint flags);

        public WindowPin(Window window, bool pinned, Action<int> reportError)
        {
            this.window = window; this.pinned = pinned; this.reportError = reportError;
            window.Topmost = pinned;
            window.SourceInitialized += OnSourceInitialized;
            window.Loaded += OnLoaded;
            window.IsVisibleChanged += OnVisibilityChanged;
            window.Closed += OnClosed;
            AttachSource();
        }

        public bool SetPinned(bool value)
        {
            pinned = value;
            window.Topmost = value;
            return Apply(true);
        }

        void AttachSource()
        {
            if (source != null || disposed) return;
            var handle = new WindowInteropHelper(window).Handle;
            if (handle == IntPtr.Zero) return;
            source = HwndSource.FromHwnd(handle);
            if (source != null) source.AddHook(OnMessage);
        }

        void OnSourceInitialized(object sender, EventArgs e) { AttachSource(); QueueCheck(); }
        void OnLoaded(object sender, RoutedEventArgs e) { Apply(true); }
        void OnVisibilityChanged(object sender, DependencyPropertyChangedEventArgs e)
        {
            if (window.IsVisible) QueueCheck();
        }

        IntPtr OnMessage(IntPtr hwnd, int message, IntPtr wParam, IntPtr lParam, ref bool handled)
        {
            // WINDOWPOSCHANGED, STYLECHANGED, DISPLAYCHANGE, DWMCOMPOSITIONCHANGED.
            if (message == 0x0047 || message == 0x007D || message == 0x007E || message == 0x031E)
                QueueCheck();
            return IntPtr.Zero;
        }

        void QueueCheck()
        {
            if (disposed || applying || pending || !window.IsVisible) return;
            pending = true;
            window.Dispatcher.BeginInvoke(DispatcherPriority.Background, new Action(delegate
            {
                pending = false;
                if (!disposed && window.IsVisible) Apply(false);
            }));
        }

        bool Apply(bool force)
        {
            if (disposed || source == null || source.IsDisposed) return true;
            var nativePinned = (GetWindowLong(source.Handle, ExStyle) & WsExTopmost) != 0;
            if (!force && nativePinned == pinned) return true;
            applying = true;
            try
            {
                var ok = SetWindowPos(source.Handle, new IntPtr(pinned ? -1 : -2), 0, 0, 0, 0, PositionFlags);
                if (ok) { lastError = 0; return true; }
                var error = Marshal.GetLastWin32Error();
                if (error != lastError && reportError != null) reportError(error);
                lastError = error;
                return false;
            }
            finally { applying = false; }
        }

        void OnClosed(object sender, EventArgs e) { Dispose(); }
        public void Dispose()
        {
            if (disposed) return;
            disposed = true;
            window.SourceInitialized -= OnSourceInitialized;
            window.Loaded -= OnLoaded;
            window.IsVisibleChanged -= OnVisibilityChanged;
            window.Closed -= OnClosed;
            if (source != null && !source.IsDisposed) source.RemoveHook(OnMessage);
            source = null;
        }
    }
}
