using System;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Media;
using System.Windows.Media.Animation;

namespace AgentHud
{
    // Remaining-quota ring in the platform's own colours, with the platform's mark in the centre.
    // The percentage itself is shown as text next to the ring.
    public class QuotaRing : FrameworkElement
    {
        // Animated stand-in for Percent, so the arc moves to new values instead of jumping.
        public static readonly DependencyProperty DisplayProperty = DependencyProperty.Register("Display", typeof(double), typeof(QuotaRing), new FrameworkPropertyMetadata(0.0, FrameworkPropertyMetadataOptions.AffectsRender));
        public double Display { get { return (double)GetValue(DisplayProperty); } set { SetValue(DisplayProperty, value); } }
        public Geometry Logo { get; set; }
        public Brush ArcBrush { get; set; }
        public Brush LogoBrush { get; set; }
        public Color Track { get; set; }
        public double? Percent { get; private set; }
        public bool Stale { get; private set; }
        public string Platform { get; set; }
        Pen trackPen, arcPen;
        Color penTrack;
        Brush penArc;
        bool penUnknown;
        Geometry cachedArc;
        double cachedDisplay = Double.NaN;
        static readonly MatrixTransform LogoTransform = MakeLogoTransform();
        public QuotaRing() { Width = 34; Height = 34; }

        static MatrixTransform MakeLogoTransform()
        {
            var transform = new MatrixTransform(16.0 / 24, 0, 0, 16.0 / 24, 9, 9);
            transform.Freeze(); return transform;
        }

        public void PauseAnimation()
        {
            BeginAnimation(DisplayProperty, null);
            Display = Percent ?? 0;
        }

        public void Update(RingValue value)
        {
            var changed = Percent != value.Percent;
            var staleChanged = Stale != value.Stale;
            Percent = value.Percent; Stale = value.Stale;
            if (!Equals(ToolTip, value.Detail)) ToolTip = value.Detail;
            var amount = Percent.HasValue ? Percent.Value.ToString("0.#") + "%" : Lang.T("未提供", "not provided");
            var name = Platform + Lang.T(" 剩余额度 " + amount + (Stale ? "，历史快照" : ""), " quota remaining " + amount + (Stale ? ", snapshot" : ""));
            if (AutomationProperties.GetName(this) != name) AutomationProperties.SetName(this, name);
            if (changed)
            {
                // Critically damped: a strong ease-out, no overshoot.
                if (IsVisible && Percent.HasValue && SystemParameters.ClientAreaAnimation)
                    BeginAnimation(DisplayProperty, new DoubleAnimation(Percent.Value, TimeSpan.FromMilliseconds(600)) { EasingFunction = new QuarticEase { EasingMode = EasingMode.EaseOut } });
                else { BeginAnimation(DisplayProperty, null); Display = Percent ?? 0; }
            }
            if (changed || staleChanged) InvalidateVisual();
        }

        protected override void OnRender(DrawingContext dc)
        {
            base.OnRender(dc);
            var center = new Point(17, 17);
            const double radius = 14.8, thickness = 3.2;
            if (trackPen == null || penTrack != Track || penUnknown != !Percent.HasValue)
            {
                trackPen = new Pen(new SolidColorBrush(Track), thickness);
                if (!Percent.HasValue) { trackPen.DashStyle = new DashStyle(new double[] { 0, 1.8 }, 0); trackPen.DashCap = PenLineCap.Round; }
                trackPen.Freeze(); penTrack = Track; penUnknown = !Percent.HasValue;
            }
            dc.DrawEllipse(null, trackPen, center, radius, radius);

            dc.PushOpacity(!Percent.HasValue ? 0.4 : Stale ? 0.5 : 1);
            var value = Math.Max(0, Math.Min(100, Display));
            if (Percent.HasValue && value > 0.05 && ArcBrush != null)
            {
                if (cachedArc == null || cachedDisplay != value)
                {
                    if (value >= 99.95) cachedArc = new EllipseGeometry(center, radius, radius);
                    else
                    {
                        var angle = value / 100 * 2 * Math.PI;
                        var end = new Point(center.X + radius * Math.Sin(angle), center.Y - radius * Math.Cos(angle));
                        var geometry = new StreamGeometry();
                        using (var ctx = geometry.Open())
                        {
                            ctx.BeginFigure(new Point(center.X, center.Y - radius), false, false);
                            ctx.ArcTo(end, new Size(radius, radius), 0, value > 50, SweepDirection.Clockwise, true, false);
                        }
                        cachedArc = geometry;
                    }
                    cachedArc.Freeze(); cachedDisplay = value;
                }
                if (arcPen == null || penArc != ArcBrush)
                {
                    arcPen = new Pen(ArcBrush, thickness) { StartLineCap = PenLineCap.Round, EndLineCap = PenLineCap.Round };
                    if (arcPen.CanFreeze) arcPen.Freeze(); penArc = ArcBrush;
                }
                dc.DrawGeometry(null, arcPen, cachedArc);
            }
            if (Logo != null && LogoBrush != null)
            {
                // Logos are drawn on a 24-unit grid.
                dc.PushTransform(LogoTransform);
                dc.DrawGeometry(LogoBrush, null, Logo);
                dc.Pop();
            }
            dc.Pop();
        }
    }
}
