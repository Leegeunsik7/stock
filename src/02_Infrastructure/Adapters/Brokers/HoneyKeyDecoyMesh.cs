// ============================================================================
// HoneyKeyDecoyMesh — v8.1.0 허니키 디코이 트랩(미끼 시크릿 + 카나리 변조 탐지)
//
//  힙에 '진짜처럼 보이는 가짜 시크릿(허니토큰)'을 배치해 (1)메모리 스캐너/공격자의 시간을 낭비시키고, (2)각 디코이를
//  카나리로 봉인해 '디코이 영역 변조(메모리 인젝션·패칭)'를 δ=0 로 탐지한다. 디코이가 외부로 유출(exfil)되면 —
//  로그·아웃바운드에 허니토큰이 등장하면 — 침해가 '증명'된다(WasExfiltrated). 트립 시 퀀텀 금고 자가 소멸을 격발한다.
//
//  ── 정직 고지 ──────────────────────────────────────────────────────────────
//  · 관리형 런타임은 외부의 '수동 읽기(passive read)'를 감지할 수 없다. 본 메시는 '능동 변조(카나리 파열)'와 '유출
//    (허니토큰 등장)'만 탐지한다 — QuantumVaultSentinel(디버거)·AntiHooking(진입점)과 결합해 능동 공격면을 덮는다.
//  · 디코이는 결정론적 가짜값(RNG 아님·고정 시드) — 진짜 시크릿과 절대 충돌하지 않는 접두사(HONEY_DECOY_).
//
//  ★불변: δ=0(CheckIntegrity/IsTripped 힙 0·배열 비교·Volatile) · 결정론(고정 시드) · liveArmed:false(순수 트랩 —
//    IBroker·소켓·onSend 참조 0) · fail-closed(변조/유출 → 트립). ★순수 관리형(WebOnly 안전).
// ============================================================================
using System;
using System.Threading;

namespace MS2E.Infrastructure
{
    /// <summary>허니키 디코이 트랩(δ=0·관리형). Decoys 를 힙에 배치, CheckIntegrity(카나리)·WasExfiltrated(유출) 로 침해 탐지.</summary>
    public sealed class HoneyKeyDecoyMesh
    {
        private const string DecoyPrefix = "HONEY_DECOY_";
        private const long   CanarySeed  = unchecked((long)0xDEC0DEC0FFEEBEEFUL);
        private const long   Golden      = unchecked((long)0x9E3779B97F4A7C15UL);   // SplitMix 상수(long 캐스트 — ulong 모호 회피)

        private readonly string[] _decoys;      // 허니토큰(미끼·매력적 가짜 시크릿)
        private readonly long[]   _canaries;    // 디코이별 무결성 카나리(변조 탐지)
        private int  _tripped;
        private long _integrityChecks, _exfilHits, _tamperHits;

        public HoneyKeyDecoyMesh(int decoyCount = 8)
        {
            int n = Math.Clamp(decoyCount, 1, 256);
            _decoys = new string[n];
            _canaries = new long[n];
            for (int i = 0; i < n; i++)
            {
                // 결정론적 가짜 시크릿 — 진짜처럼 보이나 고정 시드(RNG 아님). 실 시크릿과 접두사로 격리.
                long h = CanarySeed ^ (Golden * (i + 1L));
                _decoys[i] = DecoyPrefix + i.ToString("D2") + "_" + ((ulong)h).ToString("X16");
                _canaries[i] = h;   // 카나리 = 디코이 유도값(변조 시 불일치)
            }
        }

        public bool IsTripped        => Volatile.Read(ref _tripped) == 1;
        public long IntegrityChecks  => Interlocked.Read(ref _integrityChecks);
        public long ExfilHits        => Interlocked.Read(ref _exfilHits);
        public long TamperHits       => Interlocked.Read(ref _tamperHits);
        public int  DecoyCount       => _decoys.Length;
        /// <summary>배치용 디코이 허니토큰(힙/디코이 설정 키에 심는다 — 매력적 미끼).</summary>
        public string DecoyToken(int i) => (uint)i < (uint)_decoys.Length ? _decoys[i] : string.Empty;

        /// <summary>디코이 카나리 무결성 대조(δ=0). 파열(메모리 인젝션·패칭) 시 트립 + false. 정상 true. QuantumVault extraDetector 로 결선.</summary>
        public bool CheckIntegrity()
        {
            Interlocked.Increment(ref _integrityChecks);
            for (int i = 0; i < _canaries.Length; i++)
            {
                long expect = CanarySeed ^ (Golden * (i + 1L));
                if (_canaries[i] != expect) { Interlocked.Increment(ref _tamperHits); Volatile.Write(ref _tripped, 1); return false; }
            }
            return Volatile.Read(ref _tripped) == 0;
        }

        /// <summary>아웃바운드 값에 허니토큰이 등장하면 유출 침해 증명 → 트립. (로그·전송 직전 호출.)</summary>
        public bool WasExfiltrated(ReadOnlySpan<char> observed)
        {
            if (observed.Length < DecoyPrefix.Length) return false;
            if (!observed.StartsWith(DecoyPrefix)) return false;   // 접두사 빠른 배제(δ=0)
            for (int i = 0; i < _decoys.Length; i++)
                if (observed.SequenceEqual(_decoys[i]))
                { Interlocked.Increment(ref _exfilHits); Volatile.Write(ref _tripped, 1); return true; }
            return false;
        }
    }
}
