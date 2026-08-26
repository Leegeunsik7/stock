// ============================================================================
// StockManager.Common - 통합 알림 센터
// 모든 프로그램이 공유하는 싱글톤 알림 버스
// 추가기능 A: 실시간 알림 센터
// ============================================================================
using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using System.Windows.Forms;

namespace MS2E.Infrastructure
{
    public class AlertCenter
    {
        private static readonly Lazy<AlertCenter> _instance = new(() => new AlertCenter());
        public static AlertCenter Instance => _instance.Value;

        private readonly ConcurrentQueue<AlertItem> _recent = new();
        private readonly int _maxInMemory = 500;
        private LocalDatabase? _db;

        public event EventHandler<AlertItem>? AlertRaised;
        public int UnreadCount => _db?.GetUnreadAlertCount() ?? 0;

        private AlertCenter() { }

        public void Attach(LocalDatabase db) => _db = db;

        public void Raise(AlertItem item)
        {
            _recent.Enqueue(item);
            while (_recent.Count > _maxInMemory) _recent.TryDequeue(out _);
            try { _db?.AddAlert(item); } catch (Exception ex) { Log.Warn($"Alert DB 저장 실패: {ex.Message}"); }
            try { AlertRaised?.Invoke(this, item); } catch { }
        }

        public void RaisePrice(string symbol, string title, string msg, AlertLevel level = AlertLevel.Info) =>
            Raise(new AlertItem { Source = AlertSource.Price, Level = level,
                Symbol = symbol, Title = title, Message = msg });

        public void RaiseSignal(string symbol, string title, string msg, AlertLevel level = AlertLevel.Info) =>
            Raise(new AlertItem { Source = AlertSource.Signal, Level = level,
                Symbol = symbol, Title = title, Message = msg });

        public void RaiseNews(string symbol, string title, string msg, string url = "") =>
            Raise(new AlertItem { Source = AlertSource.News, Symbol = symbol,
                Title = title, Message = msg, ActionUrl = url });

        public void RaiseDividend(string symbol, string title, string msg) =>
            Raise(new AlertItem { Source = AlertSource.Dividend, Symbol = symbol,
                Title = title, Message = msg });

        public void RaiseSystem(string title, string msg, AlertLevel level = AlertLevel.Info) =>
            Raise(new AlertItem { Source = AlertSource.System, Level = level,
                Title = title, Message = msg });

        public List<AlertItem> GetRecent(int count = 50) =>
            _recent.Reverse().Take(count).ToList();

        public List<AlertItem> GetFromDb(int count = 100, bool unreadOnly = false) =>
            _db?.GetAlerts(count, unreadOnly) ?? new List<AlertItem>();

        public void MarkAllRead() => _db?.MarkAllAlertsRead();
    }

    /// <summary>트레이 알림 (NotifyIcon 래퍼)</summary>
    public static class TrayNotifier
    {
        private static NotifyIcon? _icon;

        public static void Initialize()
        {
            if (_icon != null) return;
            _icon = new NotifyIcon
            {
                Icon = System.Drawing.SystemIcons.Information,
                Visible = true,
                Text = "MS2-E Stock Suite"
            };
        }

        public static void Show(AlertItem item, int timeoutMs = 5000)
        {
            Initialize();
            if (_icon == null) return;
            _icon.BalloonTipTitle = item.Title.Length > 63 ? item.Title[..60] + "..." : item.Title;
            _icon.BalloonTipText = item.Message.Length > 255 ? item.Message[..252] + "..." : item.Message;
            _icon.BalloonTipIcon = item.Level switch
            {
                AlertLevel.Critical => ToolTipIcon.Error,
                AlertLevel.Warning => ToolTipIcon.Warning,
                _ => ToolTipIcon.Info
            };
            try { _icon.ShowBalloonTip(timeoutMs); } catch { }
        }

        public static void Dispose()
        {
            _icon?.Dispose();
            _icon = null;
        }
    }
}
