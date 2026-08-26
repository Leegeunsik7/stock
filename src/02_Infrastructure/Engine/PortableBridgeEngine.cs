// ============================================================================
// PortableBridgeEngine — S12 #3 단독 파일 포터블 브릿지 (순수 C#, 외부 의존 0)
//  · .qbridge 바이너리: [Magic 8B][Salt 16B][IV 16B][AES-256-CBC ciphertext]
//  · PBKDF2-SHA256(100_000 iter, 16B salt → 32B key)
//  · JSON 페이로드: QBridgePackage { SchemaVersion, ExportedAt, Strategies[], Forecasts[] }
//  · 임포트 순서: 복호화 → 전략 전수 증명서 검증(실패 시 즉시 예외, DB 미수정)
//                → RunInTransaction으로 예측 원자 머지 → .ms2cart 저장
// ============================================================================
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace StockManager.Common
{
    // ── 예측 레코드 JSON DTO (DateTime → ISO8601 문자열, 교차 런타임 안정) ─────────────
    public sealed class QBridgeForecast
    {
        public string  ForecastId      { get; init; } = "";
        public string  CreatedAt       { get; init; } = "";
        public int     EngineType      { get; init; }
        public string  Symbol          { get; init; } = "";
        public string  TargetDate      { get; init; } = "";
        public int     HorizonDays     { get; init; }
        public double  PredictedValue  { get; init; }
        public double  ConfidenceLow   { get; init; }
        public double  ConfidenceHigh  { get; init; }
        public string  ScenarioTag     { get; init; } = "";
        public string  CaptureMetaJson { get; init; } = "{}";
        public double? ActualValue     { get; init; }
        public string? EvaluatedAt     { get; init; }
        public double? AbsoluteError   { get; init; }
        public string  Note            { get; init; } = "";

        public static QBridgeForecast From(ForecastRecord r) => new()
        {
            ForecastId      = r.ForecastId,
            CreatedAt       = r.CreatedAt.ToString("o"),
            EngineType      = (int)r.EngineType,
            Symbol          = r.Symbol,
            TargetDate      = r.TargetDate.ToString("o"),
            HorizonDays     = r.HorizonDays,
            PredictedValue  = r.PredictedValue,
            ConfidenceLow   = r.ConfidenceLow,
            ConfidenceHigh  = r.ConfidenceHigh,
            ScenarioTag     = r.ScenarioTag,
            CaptureMetaJson = r.CaptureMetaJson,
            ActualValue     = r.ActualValue,
            EvaluatedAt     = r.EvaluatedAt?.ToString("o"),
            AbsoluteError   = r.AbsoluteError,
            Note            = r.Note,
        };

        public ForecastRecord ToForecastRecord() => new(
            ForecastId:      ForecastId,
            CreatedAt:       DateTime.Parse(CreatedAt, null,
                                 System.Globalization.DateTimeStyles.RoundtripKind),
            EngineType:      (ForecastEngineType)EngineType,
            Symbol:          Symbol,
            TargetDate:      DateTime.Parse(TargetDate, null,
                                 System.Globalization.DateTimeStyles.RoundtripKind),
            HorizonDays:     HorizonDays,
            PredictedValue:  PredictedValue,
            ConfidenceLow:   ConfidenceLow,
            ConfidenceHigh:  ConfidenceHigh,
            ScenarioTag:     ScenarioTag,
            CaptureMetaJson: CaptureMetaJson,
            ActualValue:     ActualValue,
            EvaluatedAt:     EvaluatedAt != null
                                 ? DateTime.Parse(EvaluatedAt, null,
                                       System.Globalization.DateTimeStyles.RoundtripKind)
                                 : (DateTime?)null,
            AbsoluteError:   AbsoluteError,
            Note:            Note
        );
    }

    // ── AES 암호화 전 JSON 페이로드 ────────────────────────────────────────────────
    public sealed class QBridgePackage
    {
        public string            SchemaVersion { get; init; } = "1";
        public string            ExportedAt    { get; init; } = "";
        public CartStrategy[]    Strategies    { get; init; } = Array.Empty<CartStrategy>();
        public QBridgeForecast[] Forecasts     { get; init; } = Array.Empty<QBridgeForecast>();
    }

    // ── 임포트 결과 ────────────────────────────────────────────────────────────────
    public sealed record BridgeImportResult(
        bool   Success,
        int    StrategiesImported,
        int    ForecastsImported,
        string Note);

    // ── 핵심 엔진 ──────────────────────────────────────────────────────────────────
    public static class PortableBridgeEngine
    {
        // 파일 포맷 식별자 "QBRIDGE1" (8바이트 ASCII)
        private static readonly byte[] Magic = Encoding.ASCII.GetBytes("QBRIDGE1");
        private const int SaltLen    = 16;
        private const int IvLen      = 16;
        private const int Iterations = 100_000;

        private static readonly JsonSerializerOptions JsonOpts = new()
        {
            PropertyNamingPolicy   = null,
            WriteIndented          = false,
            DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        };

        // ── 익스포트 ────────────────────────────────────────────────────────────────
        public static byte[] Export(
            IReadOnlyList<CartStrategy>   strategies,
            IReadOnlyList<ForecastRecord> forecasts,
            string password)
        {
            if (string.IsNullOrEmpty(password))
                throw new ArgumentException("비밀번호를 입력하세요.", nameof(password));

            var pkg = new QBridgePackage
            {
                ExportedAt = DateTime.UtcNow.ToString("o"),
                Strategies = strategies.ToArray(),
                Forecasts  = forecasts.Select(QBridgeForecast.From).ToArray(),
            };

            var plain  = Encoding.UTF8.GetBytes(JsonSerializer.Serialize(pkg, JsonOpts));
            var salt   = RandomBytes(SaltLen);
            var iv     = RandomBytes(IvLen);
            var key    = DeriveKey(password, salt);
            var cipher = Encrypt(plain, key, iv);

            using var ms = new MemoryStream(Magic.Length + SaltLen + IvLen + cipher.Length);
            ms.Write(Magic, 0, Magic.Length);
            ms.Write(salt,  0, salt.Length);
            ms.Write(iv,    0, iv.Length);
            ms.Write(cipher, 0, cipher.Length);
            return ms.ToArray();
        }

        // ── 임포트 ────────────────────────────────────────────────────────────────
        //  cartridgeDir: null → CartridgeService.Dir (기본값). 테스트에서 임시 경로 지정.
        public static BridgeImportResult Import(
            byte[] fileBytes,
            string password,
            LocalDatabase db,
            string? cartridgeDir = null)
        {
            // 1. 최소 길이 + 매직 검사
            int minLen = Magic.Length + SaltLen + IvLen + 16; // 16 = AES 최소 블록
            if (fileBytes.Length < minLen)
                throw new InvalidDataException(".qbridge 파일이 너무 짧습니다.");

            for (int i = 0; i < Magic.Length; i++)
                if (fileBytes[i] != Magic[i])
                    throw new InvalidDataException("유효하지 않은 .qbridge 파일 (매직 불일치).");

            // 2. salt·IV 추출
            var salt        = fileBytes[Magic.Length..(Magic.Length + SaltLen)];
            var iv          = fileBytes[(Magic.Length + SaltLen)..(Magic.Length + SaltLen + IvLen)];
            var cipherBytes = fileBytes[(Magic.Length + SaltLen + IvLen)..];

            // 3. 복호화
            byte[] plain;
            try { plain = Decrypt(cipherBytes, DeriveKey(password, salt), iv); }
            catch (Exception ex)
            { throw new InvalidDataException("복호화 실패 — 비밀번호 오류 또는 파일 변조.", ex); }

            // 4. JSON 역직렬화 (null 반환도 변조로 처리)
            QBridgePackage? pkgOrNull;
            try { pkgOrNull = JsonSerializer.Deserialize<QBridgePackage>(plain, JsonOpts); }
            catch (Exception ex)
            { throw new InvalidDataException("패키지 JSON 파싱 실패.", ex); }
            if (pkgOrNull is null)
                throw new InvalidDataException("패키지 JSON이 비어 있습니다.");
            var pkg = pkgOrNull;

            // 5. 전략 증명서 전수 검증 — 실패 시 DB 미수정 즉시 예외 (환각 방화벽)
            foreach (var s in pkg.Strategies)
                if (!CartridgeEngine.VerifyStrategy(s))
                    throw new InvalidDataException(
                        $"전략 '{s.Name}' 증명서 검증 실패 — 변조 또는 파라미터 불일치.");

            // 6. 원자적 예측 머지 (RunInTransaction → 부분 삽입 없음)
            int forecastCount = 0;
            if (pkg.Forecasts.Length > 0)
            {
                db.RunInTransaction(() =>
                {
                    foreach (var qf in pkg.Forecasts)
                    {
                        var r = qf.ToForecastRecord();
                        db.Execute(
                            @"INSERT OR IGNORE INTO ForecastVault
                              (ForecastId,CreatedAt,EngineType,Symbol,TargetDate,HorizonDays,
                               PredictedValue,ConfidenceLow,ConfidenceHigh,
                               ScenarioTag,CaptureMetaJson,
                               ActualValue,EvaluatedAt,AbsoluteError,Note)
                              VALUES(@id,@ca,@et,@sy,@td,@hd,@pv,@cl,@ch,@st,@cm,@av,@ea,@ae,@no)",
                            new Dictionary<string, object>
                            {
                                ["@id"] = r.ForecastId,
                                ["@ca"] = r.CreatedAt.ToString("o"),
                                ["@et"] = (int)r.EngineType,
                                ["@sy"] = r.Symbol,
                                ["@td"] = r.TargetDate.ToString("o"),
                                ["@hd"] = r.HorizonDays,
                                ["@pv"] = r.PredictedValue,
                                ["@cl"] = r.ConfidenceLow,
                                ["@ch"] = r.ConfidenceHigh,
                                ["@st"] = r.ScenarioTag,
                                ["@cm"] = r.CaptureMetaJson,
                                ["@av"] = (object?)r.ActualValue    ?? DBNull.Value,
                                ["@ea"] = r.EvaluatedAt.HasValue
                                              ? (object)r.EvaluatedAt.Value.ToString("o")
                                              : DBNull.Value,
                                ["@ae"] = (object?)r.AbsoluteError  ?? DBNull.Value,
                                ["@no"] = r.Note,
                            });
                        forecastCount++;
                    }
                });
            }

            // 7. 전략 → .ms2cart 저장
            var dir = cartridgeDir ?? CartridgeService.Dir;
            if (pkg.Strategies.Length > 0)
            {
                Directory.CreateDirectory(dir);
                var cartId = Guid.NewGuid().ToString("N");
                var cart = new Cartridge(
                    FormatVersion: CartridgeEngine.FormatVersion,
                    Id:            cartId,
                    Name:          $"bridge-import-{DateTime.UtcNow:yyyyMMddHHmmss}",
                    Author:        "PortableBridge",
                    CreatedIso:    DateTime.UtcNow.ToString("o"),
                    AuthorPubKey:  "",
                    Description:   $".qbridge 임포트 — {pkg.Strategies.Length}개 전략",
                    Macros:        new(),
                    Strategies:    pkg.Strategies.ToList(),
                    Prompts:       new(),
                    Signature:     "");
                var cartJson = JsonSerializer.Serialize(cart,
                    new JsonSerializerOptions { WriteIndented = true });
                File.WriteAllText(
                    Path.Combine(dir, $"bridge-{cartId[..8]}.ms2cart"),
                    cartJson, Encoding.UTF8);
            }

            return new BridgeImportResult(
                Success:             true,
                StrategiesImported:  pkg.Strategies.Length,
                ForecastsImported:   forecastCount,
                Note:                $"임포트 완료 — 전략 {pkg.Strategies.Length}개, 예측 {forecastCount}건 저장");
        }

        // ── 암호화 헬퍼 ──────────────────────────────────────────────────────────────
        private static byte[] DeriveKey(string password, byte[] salt)
        {
            using var pbkdf2 = new Rfc2898DeriveBytes(
                password, salt, Iterations, HashAlgorithmName.SHA256);
            return pbkdf2.GetBytes(32);
        }

        private static byte[] Encrypt(byte[] plain, byte[] key, byte[] iv)
        {
            using var aes = Aes.Create();
            aes.Key = key; aes.IV = iv;
            aes.Mode = CipherMode.CBC; aes.Padding = PaddingMode.PKCS7;
            using var enc = aes.CreateEncryptor();
            using var ms  = new MemoryStream();
            using (var cs = new CryptoStream(ms, enc, CryptoStreamMode.Write))
                cs.Write(plain, 0, plain.Length);
            return ms.ToArray();
        }

        private static byte[] Decrypt(byte[] cipher, byte[] key, byte[] iv)
        {
            using var aes = Aes.Create();
            aes.Key = key; aes.IV = iv;
            aes.Mode = CipherMode.CBC; aes.Padding = PaddingMode.PKCS7;
            using var dec = aes.CreateDecryptor();
            using var ms  = new MemoryStream();
            using (var cs = new CryptoStream(new MemoryStream(cipher), dec, CryptoStreamMode.Read))
                cs.CopyTo(ms);
            return ms.ToArray();
        }

        private static byte[] RandomBytes(int n)
        {
            var buf = new byte[n];
            RandomNumberGenerator.Fill(buf);
            return buf;
        }
    }
}
