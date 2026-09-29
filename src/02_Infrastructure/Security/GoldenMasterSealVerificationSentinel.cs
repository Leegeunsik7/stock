// ============================================================================
// GoldenMasterSealVerificationSentinel — v12.13.0 [Step 7/7] Think Beyond Gemini Idea B:
//   Production Golden Master Seal & Verification Sentinel
//
//   전체 시스템 어셈블리(DLL)의 SHA-256 해시를 산출하고, 기대 해시(빌드 시 고정된 골든 마스터 값)와 상수시간
//   비교 + liveArmed:false 불변식을 함께 검증한다. ★정직 스코핑: 이 엔진은 '판정'만 수행한다 — 실패 시 실제
//   프로세스 종료/터미널 진입 차단은 호출측(Program.cs/부팅 시퀀스)의 책임으로 남긴다. 부팅 시퀀스에 실제
//   종료 로직을 결속하는 것은 기존 검증된 부팅 경로(GoldenMasterBootSentinel 등)에 대한 고위험 변경이라
//   이번 감사 라운드에서는 판정 엔진만 제공하고 실 배선은 후속 과제로 남긴다(가짜 배선 없음).
//
//   불변: 결정론(순수 비교 — 해시 산출 자체는 파일 I/O 라 δ=0 대상 아님, 명시) · fail-closed(파싱 실패·해시
//        불일치·liveArmed=true → 전부 실패 판정) · liveArmed:false(검증이 무장을 유발하지 않음 — 오히려 무장
//        상태 자체를 이 시점에서 이상으로 취급).
// ============================================================================
using System;
using System.IO;
using System.Security.Cryptography;

namespace MS2E.Infrastructure
{
    public static class GoldenMasterSealVerificationSentinel
    {
        /// <summary>어셈블리 파일의 SHA-256 해시를 16진수 문자열로 산출(콜드 경로 — 파일 I/O, 부팅 시 1회 호출
        /// 전제. 스트리밍 해시라 파일 크기만큼의 힙 버퍼링 없이 처리).</summary>
        public static string ComputeSha256Hex(string assemblyPath)
        {
            using var stream = File.OpenRead(assemblyPath);
            byte[] hash = SHA256.HashData(stream);
            return Convert.ToHexString(hash);
        }

        /// <summary>
        /// 실제 해시와 기대 해시를 상수시간 비교 + liveArmed 불변식을 함께 검증. ★liveArmed=true 상태에서 골든
        /// 마스터 무결성 검증 자체가 호출된다는 것은 이미 절차 위반(정상 부팅 시퀀스는 무장 전에 검증을 마쳐야
        /// 함)이므로 그 자체를 실패로 판정한다. 해시 문자열 파싱 실패도 fail-closed.
        /// </summary>
        public static bool Verify(string actualHashHex, string expectedHashHex, bool liveArmed)
        {
            if (liveArmed) return false;
            byte[] actual, expected;
            try
            {
                actual = Convert.FromHexString(actualHashHex);
                expected = Convert.FromHexString(expectedHashHex);
            }
            catch (FormatException) { return false; }

            if (actual.Length != expected.Length) return false;
            return CryptographicOperations.FixedTimeEquals(actual, expected);
        }

        /// <summary>파일 경로 기준 원샷 검증(ComputeSha256Hex + Verify 결합 편의 오버로드).</summary>
        public static bool VerifyFile(string assemblyPath, string expectedHashHex, bool liveArmed)
        {
            string actual;
            try { actual = ComputeSha256Hex(assemblyPath); }
            catch (IOException) { return false; }
            catch (UnauthorizedAccessException) { return false; }
            return Verify(actual, expectedHashHex, liveArmed);
        }
    }
}
