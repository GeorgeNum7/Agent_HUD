using System;
using System.Collections.Generic;
using System.Linq;

namespace AgentHud
{
    public class Snapshot
    {
        public List<Provider> providers { get; set; }
        public double? collectedAt { get; set; }
        public bool loading { get; set; }
    }
    public class Provider
    {
        public string id { get; set; }
        public string name { get; set; }
        public bool connected { get; set; }
        public bool bridgeConnected { get; set; }
        public string quotaStatus { get; set; }
        public string quotaMessage { get; set; }
        public Limits limits { get; set; }
    }
    public class Limits
    {
        public List<QuotaWindow> windows { get; set; }
        public double? collectedAt { get; set; }
        public string source { get; set; }
        public double? staleAfterMs { get; set; }
        public bool stale { get; set; }
    }
    public class QuotaWindow
    {
        public string name { get; set; }
        public double? remainingPercent { get; set; }
        public double? windowMinutes { get; set; }
        public double? resetsAt { get; set; }
    }
    public class WidgetState
    {
        public double? left { get; set; }
        public double? top { get; set; }
        public string monitor { get; set; }
        public double? offsetX { get; set; }
        public double? offsetY { get; set; }
        public bool pinned { get; set; }
        public int period { get; set; }
        public WidgetState() { pinned = true; period = 300; }
    }
    public class RingValue
    {
        public double? Percent;
        public double? ResetsAt;
        public bool Stale;
        public string Caption;
        public string Detail;
        public static double Now() { return (DateTime.UtcNow - new DateTime(1970, 1, 1)).TotalMilliseconds; }
        static readonly DateTime Epoch = new DateTime(1970, 1, 1, 0, 0, 0, DateTimeKind.Utc);
        static readonly string[] WeekDays = { "周日", "周一", "周二", "周三", "周四", "周五", "周六" };

        public static RingValue From(Provider provider, int period, double now, bool online)
        {
            var result = new RingValue { Caption = "等待数据", Detail = "等待平台提供额度，未将未知值当作零。" };
            if (provider == null) return result;
            var limits = provider.limits;
            var win = limits == null || limits.windows == null ? null : limits.windows.FirstOrDefault(w => w.windowMinutes == period);
            if (win == null || !win.remainingPercent.HasValue || Double.IsNaN(win.remainingPercent.Value) || Double.IsInfinity(win.remainingPercent.Value))
            {
                result.Caption = provider.quotaStatus == "desktop-connection-disabled" ? "等待连接" : provider.quotaStatus == "desktop-not-connected" ? "桌面版未接入" : "额度未提供";
                result.Detail = provider.quotaMessage ?? "这个平台还没有提供所选周期的额度。可打开完整面板查看接入状态。";
                result.Detail += "\n" + UpdatedText(limits == null ? null : limits.collectedAt, now);
                result.Detail += provider.id == "claude" ? "\n右键小组件 → Claude 官方用量，查看当前账户额度。" : "\n右键小组件 → Codex 官方用量，查看当前账户额度。";
                return result;
            }
            if (win.resetsAt.HasValue && win.resetsAt.Value <= now)
            {
                result.Caption = "等待额度重置";
                result.Detail = "上次快照的重置时间已过，等待新数据；不会自动显示 100%。\n" + UpdatedText(limits.collectedAt, now);
                return result;
            }
            result.Percent = Math.Max(0, Math.Min(100, win.remainingPercent.Value));
            result.ResetsAt = win.resetsAt;
            result.Stale = limits.stale || !online || !limits.collectedAt.HasValue || now - limits.collectedAt.Value > (limits.staleAfterMs ?? 120000);
            result.Caption = win.resetsAt.HasValue ? ResetText(win.resetsAt.Value - now) : "重置时间未提供";
            var resetDate = win.resetsAt.HasValue ? LocalTime(win.resetsAt.Value) : null;
            result.Detail = (limits.source ?? "平台额度") + "\n" + UpdatedText(limits.collectedAt, now) + "\n" + (result.Stale ? "历史快照 · " : "") + result.Caption + (resetDate.HasValue ? "（" + resetDate.Value.ToString("MM-dd HH:mm") + "）" : "");
            if (limits.source == "Codex 官方额度接口") result.Detail += "\n正常约每 1 分钟查询，连接失败时可能延迟。";
            else if (limits.source == "Claude 桌面账户额度") result.Detail += "\n正常约每 5 分钟查询，连接失败时可能延迟。";
            result.Detail += "\n同一平台账户内共享，非单个 agent 的独立额度。";
            if (!String.IsNullOrEmpty(provider.quotaMessage)) result.Detail += "\n" + provider.quotaMessage;
            if (result.Stale) result.Caption = "快照 · " + result.Caption;
            return result;
        }
        public static string UpdatedText(double? collectedAt, double now)
        {
            if (!collectedAt.HasValue) return "额度更新时间未知";
            var stamp = collectedAt.Value;
            if (Double.IsNaN(stamp) || Double.IsInfinity(stamp) || stamp > now + 60000) return "额度更新时间异常，请检查系统时间";
            try
            {
                var date = new DateTime(1970, 1, 1, 0, 0, 0, DateTimeKind.Utc).AddMilliseconds(stamp).ToLocalTime();
                var minutes = Math.Floor(Math.Max(0, now - stamp) / 60000);
                var age = minutes < 1 ? "刚刚" : minutes < 60 ? minutes + " 分钟前" : minutes < 1440 ? Math.Floor(minutes / 60) + " 小时前" : Math.Floor(minutes / 1440) + " 天前";
                return "额度更新：" + age + "（" + date.ToString("MM-dd HH:mm") + "）";
            }
            catch (ArgumentOutOfRangeException) { return "额度更新时间未知"; }
        }
        static DateTime? LocalTime(double stamp)
        {
            if (Double.IsNaN(stamp) || Double.IsInfinity(stamp)) return null;
            try { return Epoch.AddMilliseconds(stamp).ToLocalTime(); }
            catch (ArgumentOutOfRangeException) { return null; }
        }
        // Short form for the strip (≤ 52 DIP): the clock time within a day, the weekday beyond it,
        // like Claude's own usage page. The exact countdown and date stay in the tooltip.
        public static string ResetBadge(double resetsAt, double now)
        {
            var reset = LocalTime(resetsAt);
            if (!reset.HasValue || resetsAt <= now) return null;
            if (resetsAt - now < 86400000) return reset.Value.ToString("HH:mm", System.Globalization.CultureInfo.InvariantCulture) + " 重置";
            // A 7-day window can land on today's weekday next week.
            var nextWeek = (reset.Value.Date - LocalTime(now).Value.Date).TotalDays >= 7;
            return (nextWeek ? "下" : "") + WeekDays[(int)reset.Value.DayOfWeek] + "重置";
        }
        public static string ResetText(double milliseconds)
        {
            var minutes = (long)Math.Ceiling(Math.Max(0, milliseconds) / 60000);
            if (minutes >= 1440) return String.Format("{0}天 {1}小时后重置", minutes / 1440, (minutes % 1440) / 60);
            if (minutes >= 60) return String.Format("{0}小时 {1}分后重置", minutes / 60, minutes % 60);
            return String.Format("{0}分钟后重置", minutes);
        }
    }
}
