using System;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Markup;
using System.Windows.Media;
using Microsoft.Win32;

namespace AgentHud
{
    // Apple tokens (same as the dashboard, see public/styles.css): grey-white glass, hairline edge,
    // grayscale text, one blue accent, heat orange for a low quota. Follows the Windows *app* theme so
    // the widget matches the dashboard.
    public sealed class Palette
    {
        public bool Light;
        public Color Text, TextMuted, Accent, Low;
        public Brush Tint, Sheen, Rim, RimOffline, Edge;
        // Platform colours for the rings: Codex's blue-violet gradient, Claude's clay.
        public Brush CodexArc, CodexLogo, ClaudeArc, ClaudeLogo;
        public Color CodexTrack, ClaudeTrack;

        public static Palette Read()
        {
            bool light = true, transparency = true;
            try
            {
                using (var key = Registry.CurrentUser.OpenSubKey(@"Software\Microsoft\Windows\CurrentVersion\Themes\Personalize"))
                {
                    if (key != null) { light = !Equals(key.GetValue("AppsUseLightTheme"), 0); transparency = !Equals(key.GetValue("EnableTransparency"), 0); }
                }
            }
            catch { }
            var palette = light ? LightGlass(transparency) : DarkGlass(transparency);
            // Codex brand gradient (lavender → periwinkle → ultramarine), top to bottom like its mark.
            palette.CodexArc = Gradient(new Point(17, 2), new Point(17, 32));
            palette.CodexLogo = Gradient(new Point(12, 3), new Point(12, 21));
            palette.ClaudeArc = palette.ClaudeLogo = Solid(Hex(0xd97757));
            var trackAlpha = (byte)(light ? 48 : 72);
            palette.CodexTrack = Color.FromArgb(trackAlpha, 0x7a, 0x9d, 0xff);
            palette.ClaudeTrack = Color.FromArgb(trackAlpha, 0xd9, 0x77, 0x57);
            // Windows high contrast (the counterpart of Increase Contrast / Show Borders): opaque backing and a visible edge.
            if (SystemParameters.HighContrast)
            {
                palette.Tint = Solid(light ? Colors.White : Colors.Black);
                palette.Edge = Solid(palette.Text);
            }
            return palette;
        }

        static Brush Gradient(Point start, Point end)
        {
            var brush = new LinearGradientBrush { MappingMode = BrushMappingMode.Absolute, StartPoint = start, EndPoint = end };
            brush.GradientStops.Add(new GradientStop(Hex(0xb1a7ff), 0)); brush.GradientStops.Add(new GradientStop(Hex(0x7a9dff), .5)); brush.GradientStops.Add(new GradientStop(Hex(0x3941ff), 1));
            brush.Freeze(); return brush;
        }

        static Palette LightGlass(bool transparency)
        {
            return new Palette
            {
                Light = true,
                Text = Hex(0x1d1d1f), TextMuted = Hex(0x86868b),
                // Low quota shows in system red: orange is Claude's own colour.
                Accent = Hex(0x0071e3), Low = Hex(0xff3b30),
                // Glass recipe: rgba(245,245,247,.72); solid when Windows transparency effects are off.
                Tint = Solid(Color.FromArgb((byte)(transparency ? 184 : 255), 245, 245, 247)),
                Sheen = Brushes.Transparent, Rim = Brushes.Transparent,
                RimOffline = Solid(Color.FromArgb(220, 255, 107, 0)),
                Edge = Solid(Color.FromArgb(20, 0, 0, 0)),
            };
        }

        static Palette DarkGlass(bool transparency)
        {
            return new Palette
            {
                Light = false,
                Text = Hex(0xf5f5f7), TextMuted = Hex(0x98989d),
                Accent = Hex(0x0a84ff), Low = Hex(0xff453a),
                Tint = Solid(Color.FromArgb((byte)(transparency ? 184 : 255), 28, 28, 30)),
                Sheen = Brushes.Transparent, Rim = Brushes.Transparent,
                RimOffline = Solid(Color.FromArgb(220, 255, 159, 10)),
                Edge = Solid(Color.FromArgb(30, 255, 255, 255)),
            };
        }

        // Brushes the XAML styles (menus, tooltips, chips) look up dynamically.
        public void ApplyTo(ResourceDictionary resources)
        {
            resources["Text"] = Solid(Text);
            resources["TextMuted"] = Solid(TextMuted);
            resources["Accent"] = Solid(Accent);
            resources["Low"] = Solid(Low);
            // Segmented "white pill": solid surface, hairline, no gradient.
            resources["ChipBg"] = Solid(Light ? Colors.White : Color.FromArgb(36, 255, 255, 255));
            resources["ChipHover"] = Solid(Light ? Hex(0xfbfbfd) : Color.FromArgb(52, 255, 255, 255));
            resources["ChipRim"] = Solid(Light ? Color.FromArgb(20, 0, 0, 0) : Color.FromArgb(26, 255, 255, 255));
            resources["IconHover"] = Solid(Light ? Color.FromArgb(13, 0, 0, 0) : Color.FromArgb(20, 255, 255, 255));
            resources["MenuBg"] = Solid(Light ? Color.FromArgb(250, 251, 251, 253) : Color.FromArgb(250, 44, 44, 46));
            resources["MenuRim"] = Solid(Light ? Color.FromArgb(18, 0, 0, 0) : Color.FromArgb(26, 255, 255, 255));
            resources["MenuHover"] = Solid(Light ? Color.FromArgb(13, 0, 0, 0) : Color.FromArgb(20, 255, 255, 255));
            resources["MenuSeparator"] = Solid(Light ? Color.FromArgb(18, 0, 0, 0) : Color.FromArgb(26, 255, 255, 255));
        }

        static Color Hex(int rgb) { return Color.FromRgb((byte)(rgb >> 16), (byte)(rgb >> 8), (byte)rgb); }
        static Brush Solid(Color color) { var brush = new SolidColorBrush(color); brush.Freeze(); return brush; }

        public const string Styles = @"
<ResourceDictionary xmlns='http://schemas.microsoft.com/winfx/2006/xaml/presentation' xmlns:x='http://schemas.microsoft.com/winfx/2006/xaml'>
  <Style TargetType='ContextMenu'>
    <Setter Property='OverridesDefaultStyle' Value='True'/>
    <Setter Property='HasDropShadow' Value='False'/>
    <Setter Property='FontFamily' Value='Segoe UI Variable Text, Segoe UI, Microsoft YaHei UI'/>
    <Setter Property='Template'><Setter.Value><ControlTemplate TargetType='ContextMenu'>
      <Border Margin='14' Padding='5' CornerRadius='12' Background='{DynamicResource MenuBg}' BorderBrush='{DynamicResource MenuRim}' BorderThickness='1'>
        <Border.Effect><DropShadowEffect BlurRadius='28' ShadowDepth='8' Direction='270' Opacity='.2' Color='Black'/></Border.Effect>
        <StackPanel IsItemsHost='True' KeyboardNavigation.DirectionalNavigation='Cycle'/>
      </Border>
    </ControlTemplate></Setter.Value></Setter>
  </Style>
  <Style TargetType='MenuItem'>
    <Setter Property='OverridesDefaultStyle' Value='True'/>
    <Setter Property='Foreground' Value='{DynamicResource Text}'/>
    <Setter Property='FontSize' Value='12.5'/>
    <Setter Property='Template'><Setter.Value><ControlTemplate TargetType='MenuItem'>
      <Border x:Name='Bd' CornerRadius='6' Padding='8,7,18,7' MinWidth='176' Background='Transparent'>
        <Grid>
          <Grid.ColumnDefinitions><ColumnDefinition Width='22'/><ColumnDefinition Width='*'/></Grid.ColumnDefinitions>
          <Path x:Name='Check' Visibility='Hidden' Data='M1,5 L4.5,8.5 L11,1.5' Stroke='{DynamicResource Accent}' StrokeThickness='1.8' StrokeStartLineCap='Round' StrokeEndLineCap='Round' StrokeLineJoin='Round' VerticalAlignment='Center' HorizontalAlignment='Left' Margin='2,0,0,0'/>
          <ContentPresenter Grid.Column='1' ContentSource='Header' RecognizesAccessKey='True' VerticalAlignment='Center'/>
        </Grid>
      </Border>
      <ControlTemplate.Triggers>
        <Trigger Property='IsHighlighted' Value='True'><Setter TargetName='Bd' Property='Background' Value='{DynamicResource MenuHover}'/></Trigger>
        <Trigger Property='IsChecked' Value='True'><Setter TargetName='Check' Property='Visibility' Value='Visible'/></Trigger>
        <Trigger Property='IsEnabled' Value='False'><Setter Property='Opacity' Value='.45'/></Trigger>
      </ControlTemplate.Triggers>
    </ControlTemplate></Setter.Value></Setter>
  </Style>
  <Style x:Key='{x:Static MenuItem.SeparatorStyleKey}' TargetType='Separator'>
    <Setter Property='OverridesDefaultStyle' Value='True'/>
    <Setter Property='Template'><Setter.Value><ControlTemplate TargetType='Separator'>
      <Border Height='1' Margin='10,4' Background='{DynamicResource MenuSeparator}'/>
    </ControlTemplate></Setter.Value></Setter>
  </Style>
  <Style TargetType='ToolTip'>
    <Setter Property='OverridesDefaultStyle' Value='True'/>
    <Setter Property='HasDropShadow' Value='False'/>
    <Setter Property='Foreground' Value='{DynamicResource Text}'/>
    <Setter Property='FontFamily' Value='Segoe UI Variable Text, Segoe UI, Microsoft YaHei UI'/>
    <Setter Property='FontSize' Value='12'/>
    <Setter Property='Template'><Setter.Value><ControlTemplate TargetType='ToolTip'>
      <Border Margin='14' Padding='12,8,12,9' CornerRadius='12' MaxWidth='320' Background='{DynamicResource MenuBg}' BorderBrush='{DynamicResource MenuRim}' BorderThickness='1'>
        <Border.Effect><DropShadowEffect BlurRadius='24' ShadowDepth='6' Direction='270' Opacity='.18' Color='Black'/></Border.Effect>
        <ContentPresenter><ContentPresenter.Resources><Style TargetType='TextBlock'><Setter Property='TextWrapping' Value='Wrap'/></Style></ContentPresenter.Resources></ContentPresenter>
      </Border>
    </ControlTemplate></Setter.Value></Setter>
  </Style>
  <Style x:Key='GlassChip' TargetType='Button'>
    <Setter Property='OverridesDefaultStyle' Value='True'/>
    <Setter Property='Foreground' Value='{DynamicResource Text}'/>
    <Setter Property='Cursor' Value='Hand'/>
    <Setter Property='Focusable' Value='False'/>
    <Setter Property='RenderTransformOrigin' Value='.5,.5'/>
    <Setter Property='Template'><Setter.Value><ControlTemplate TargetType='Button'>
      <Grid Background='Transparent'>
        <Border x:Name='Bd' Width='36' Height='26' CornerRadius='13' Background='{DynamicResource ChipBg}' BorderBrush='{DynamicResource ChipRim}' BorderThickness='1'>
          <Border.Effect><DropShadowEffect BlurRadius='3' ShadowDepth='1' Direction='270' Opacity='.12' Color='Black'/></Border.Effect>
        </Border>
        <ContentPresenter HorizontalAlignment='Center' VerticalAlignment='Center' Margin='0,0,0,1'/>
      </Grid>
      <ControlTemplate.Triggers>
        <Trigger Property='IsMouseOver' Value='True'><Setter TargetName='Bd' Property='Background' Value='{DynamicResource ChipHover}'/></Trigger>
      </ControlTemplate.Triggers>
    </ControlTemplate></Setter.Value></Setter>
  </Style>
  <Style x:Key='IconChip' TargetType='Button'>
    <Setter Property='OverridesDefaultStyle' Value='True'/>
    <Setter Property='Cursor' Value='Hand'/>
    <Setter Property='Focusable' Value='False'/>
    <Setter Property='Template'><Setter.Value><ControlTemplate TargetType='Button'>
      <Grid Background='Transparent'>
        <Border x:Name='Bd' Width='20' Height='30' CornerRadius='10' Background='Transparent'/>
        <ContentPresenter HorizontalAlignment='Center' VerticalAlignment='Center'/>
      </Grid>
      <ControlTemplate.Triggers>
        <Trigger Property='IsMouseOver' Value='True'><Setter TargetName='Bd' Property='Background' Value='{DynamicResource IconHover}'/></Trigger>
      </ControlTemplate.Triggers>
    </ControlTemplate></Setter.Value></Setter>
  </Style>
</ResourceDictionary>";

        public static ResourceDictionary LoadStyles() { return (ResourceDictionary)XamlReader.Parse(Styles); }
    }

    // Real blur of whatever is behind the strip. DWM blurs the whole window rectangle and ignores window
    // regions, so the only clean shape is the system's own rounded corner (8 DIP on Windows 11); the WPF
    // glass layers use the same radius.
    public static class GlassWindow
    {
        public const double SystemCornerRadius = 8;
        [StructLayout(LayoutKind.Sequential)] struct AccentPolicy { public int AccentState; public int AccentFlags; public uint GradientColor; public int AnimationId; }
        [StructLayout(LayoutKind.Sequential)] struct CompositionData { public int Attribute; public IntPtr Data; public int SizeOfData; }
        [DllImport("user32.dll")] static extern int SetWindowCompositionAttribute(IntPtr hwnd, ref CompositionData data);
        [DllImport("dwmapi.dll")] static extern int DwmSetWindowAttribute(IntPtr hwnd, int attribute, ref int value, int size);
        const int AccentEnableBlurBehind = 3, WcaAccentPolicy = 19;
        const int DwmwaWindowCornerPreference = 33, DwmwaBorderColor = 34, DwmwcpRound = 2, DwmwaColorNone = unchecked((int)0xFFFFFFFE);

        public static bool EnableBlur(IntPtr hwnd)
        {
            var accent = new AccentPolicy { AccentState = AccentEnableBlurBehind };
            var size = Marshal.SizeOf(accent);
            var pointer = Marshal.AllocHGlobal(size);
            try
            {
                Marshal.StructureToPtr(accent, pointer, false);
                var data = new CompositionData { Attribute = WcaAccentPolicy, Data = pointer, SizeOfData = size };
                return SetWindowCompositionAttribute(hwnd, ref data) != 0;
            }
            catch (EntryPointNotFoundException) { return false; }
            finally { Marshal.FreeHGlobal(pointer); }
        }

        // Returns false before Windows 11, where windows (and the blur) stay square.
        public static bool RoundCorners(IntPtr hwnd)
        {
            try
            {
                var round = DwmwcpRound;
                if (DwmSetWindowAttribute(hwnd, DwmwaWindowCornerPreference, ref round, sizeof(int)) != 0) return false;
                // The WPF rim draws the edge; drop the system's 1px border so the two don't double up.
                var none = DwmwaColorNone;
                DwmSetWindowAttribute(hwnd, DwmwaBorderColor, ref none, sizeof(int));
                return true;
            }
            catch (DllNotFoundException) { return false; }
        }
    }
}
