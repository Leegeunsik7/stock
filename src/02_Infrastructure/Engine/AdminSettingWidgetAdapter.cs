// ============================================================================
// AdminSettingWidgetAdapter — Phase 94-H: 설정/관리자 위젯 Port 호스트 어댑터 (DIP 구현)
//   순수 UI 의 IAdminSettingWidgetPort 를 엔진 구상(LocalDatabase·BrokerHub·ApiKeyVault·LanStatusServer·AiService)에 결속.
//
//   ★보안: API 키는 어댑터에서 마스킹한 뒤에만 노출(원본 키는 RCL 로 나가지 않음). 비밀번호류는 SetSecret→DPAPI 암호화.
//   ★수명: LocalDatabase(싱글턴) 래핑 — 어댑터도 싱글턴 등록 가능. 이벤트 구독 없음(순수 조회/기입).
//   ★liveArmed:false: 설정 조회/기입 전용 — 발주·소켓·브로커 발주 API 무접촉.
// ============================================================================
using System;
using MS2E.UI.Portable.Ports;

namespace MS2E.Infrastructure
{
    public sealed class AdminSettingWidgetAdapter : IAdminSettingWidgetPort
    {
        private readonly LocalDatabase _db;
        private readonly TightenStore _tightenStore;
        public AdminSettingWidgetAdapter(LocalDatabase db) { _db = db; _tightenStore = new TightenStore(db); }

        // ── 설정 값 ──
        public string? GetSetting(string key) => _db.GetSetting(key);
        // ★UiSettingWriteGuard 허용 목록 밖(무장 키 Dispatch.*·Broker.LiveArmed·Canary.* 등 · 미분류) 기입은 폐기(fail-closed).
        public void SetSetting(string key, string value) { if (UiSettingWriteGuard.Admit(key)) _db.SetSetting(key, value); }
        public void SetSecret(string key, string value) { if (UiSettingWriteGuard.Admit(key)) _db.SetSecret(key, value); }

        // ── [v12.18.3-DEV] 강화 전용(TightenOnlyUiSettingWriter — 판정 · 기입 직렬화 · 완화/미등재 폐기). 직접 DB 기입 0(출하 IL 가드 ⑨). ──
        public int TightenSetting(string key, string value)
        {
            var v = TightenOnlyUiSettingWriter.TryWrite(key, value, _tightenStore);
            if (v is not (TightenVerdict.Tightened or TightenVerdict.Unchanged))
                System.Diagnostics.Trace.WriteLine("[TightenOnly] 폐기 " + v + " key=" + key);   // 콜드 UI 경로 감사 로그(값 원문 미기록)
            return (int)v;
        }

        public bool TryGetTightenable(string key, out long effective)
        {
            effective = 0;
            int i = TightenOnlyUiSettingWriter.IndexOf(key);
            if (i < 0) return false;
            string? raw = Environment.GetEnvironmentVariable(key) ?? _db.GetSetting(key);
            TightenOnlyUiSettingWriter.TryEffective(i, raw is null ? ReadOnlySpan<char>.Empty : raw.AsSpan(), strict: false, out effective);
            return true;
        }

        /// <summary>호스트 precedence(환경변수 → DB)와 같은 판독 · DB 기입.</summary>
        private sealed class TightenStore : ITightenSettingStore
        {
            private readonly LocalDatabase _db;
            public TightenStore(LocalDatabase db) => _db = db;
            public string? ReadEnvironment(string key) => Environment.GetEnvironmentVariable(key);
            public string? ReadStored(string key) => _db.GetSetting(key);
            public void WriteStored(string key, string value) => _db.SetSetting(key, value);
        }

        // ── 거래 모드 ──
        public bool IsLive => BrokerHub.IsLive(_db);
        public void SetLive(bool live) => BrokerHub.SetLive(_db, live);

        // ── API 키(마스킹) ──
        public string UpbitAccessKeyMasked { get { var (access, _) = ApiKeyVault.GetUpbit(_db); return Mask(access); } }
        public string KisAccessKeyMasked { get { var (key, _, _) = ApiKeyVault.GetKis(_db); return Mask(key); } }

        // ── LAN 상태 서버 ──
        public bool LanRunning => LanStatusServer.Instance.Running;
        public int LanPort => LanStatusServer.Instance.Port;
        public string LanLastError => LanStatusServer.Instance.LastError;
        public void RestartLan() => LanStatusServer.Instance.Restart(_db);

        // ── LLM 백엔드 ──
        public string ActiveLlm => new AiService(_db).ActiveLlm;

        // 원본 키를 RCL 로 내보내지 않기 위한 어댑터 내부 마스킹(기존 SettingsPage.MaskKey 규칙 보존).
        private static string Mask(string k) =>
            string.IsNullOrWhiteSpace(k) ? "(미설정)" :
            k.Length > 8 ? k[..4] + "****" + k[^4..] : "****";
    }
}
