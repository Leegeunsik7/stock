// ============================================================================
// KisRateLimitClassifier — [v12.18.22-DEV] KIS 비정형 한도 초과 응답 분류(HTTP 429 · 또는 비성공 상태 + 본문 msg_cd = "EGW00201")
//
//  KIS 는 초당 거래건수 초과를 표준 429 가 아니라 HTTP 500 + {"rt_cd":"1","msg_cd":"EGW00201","msg1":"초당 거래건수를 초과하였습니다."} 로
//  돌려주는 경우가 있다. 이 분류기는 그 본문을 UTF-8 바이트 그대로 Utf8JsonReader 로 훑어 '최상위' msg_cd 값이 EGW00201 인지만 본다
//  (문자열 생성 0 · ValueTextEquals 바이트 비교 · CLAUDE.md §3 — 타입드 판독 · 예외는 JsonException 만 흡수).
//  ★보수성: 형식 오류 · 중첩 객체 안의 msg_cd · msg1 문구 일치만 있는 경우 = 한도 초과 아님(오분류로 백오프를 키우지 않음 ·
//   본문이 깨진 실패는 종전대로 일반 오류). 성공(2xx) 상태는 본문과 무관하게 한도 초과 아님.
//  δ=0: ref struct 판독기 · 스택 상태만. 본문 바이트 확보(HTTP 수신)는 호출측 콜드 오류 경로.
// ============================================================================
using System;
using System.Text.Json;

namespace MS2E.Infrastructure
{
    public static class KisRateLimitClassifier
    {
        public const string RateLimitMsgCode = "EGW00201";
        private static ReadOnlySpan<byte> MsgCodeName => "msg_cd"u8;
        private static ReadOnlySpan<byte> RateLimitCode => "EGW00201"u8;

        /// <summary>응답이 KIS 한도 초과인가 — 429, 또는 비성공(≥ 400) 상태 + 최상위 msg_cd == EGW00201.</summary>
        public static bool IsRateLimit(int httpStatus, ReadOnlySpan<byte> utf8Body)
        {
            if (httpStatus == BrokerBackoffController.HttpTooManyRequests) return true;
            if (httpStatus < 400) return false;
            return HasRateLimitMsgCode(utf8Body);
        }

        /// <summary>본문 최상위 msg_cd 가 EGW00201 인가(δ=0 · 형식 오류 = false).</summary>
        public static bool HasRateLimitMsgCode(ReadOnlySpan<byte> utf8Body)
        {
            if (utf8Body.Length >= 3 && utf8Body[0] == 0xEF && utf8Body[1] == 0xBB && utf8Body[2] == 0xBF) utf8Body = utf8Body[3..];   // UTF-8 BOM(판독기는 BOM 을 받지 않음)
            if (utf8Body.IsEmpty) return false;
            var r = new Utf8JsonReader(utf8Body, new JsonReaderOptions { CommentHandling = JsonCommentHandling.Skip });
            try
            {
                if (!r.Read() || r.TokenType != JsonTokenType.StartObject) return false;
                while (r.Read())
                {
                    if (r.TokenType == JsonTokenType.EndObject) return false;          // 최상위 객체 끝
                    if (r.TokenType != JsonTokenType.PropertyName) return false;
                    bool isCode = r.ValueTextEquals(MsgCodeName);
                    if (!r.Read()) return false;
                    if (isCode) return r.TokenType == JsonTokenType.String && r.ValueTextEquals(RateLimitCode);
                    if (r.TokenType is JsonTokenType.StartObject or JsonTokenType.StartArray) r.Skip();   // 중첩은 건너뜀(최상위만 판정)
                }
                return false;
            }
            catch (JsonException) { return false; }
        }
    }
}
