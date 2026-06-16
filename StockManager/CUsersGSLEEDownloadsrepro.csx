using System;
using System.Linq;
using System.Text.RegularExpressions;

var StopWords = new System.Collections.Generic.HashSet<string>(StringComparer.OrdinalIgnoreCase)
  { "RSI","MA","SMA","EMA","MACD","OOS","AI","LLM","KRW","USD","P5","P95","VS","OK","ETF" };

string ExtractSymbol(string nl){
  var krx = Regex.Match(nl, @"\b(\d{6})\b");
  if (krx.Success) return krx.Groups[1].Value;
  foreach (Match m in Regex.Matches(nl, @"\b([A-Za-z]{1,5})\b")){
    var t = m.Groups[1].Value.ToUpperInvariant();
    if (!StopWords.Contains(t)) return t;
  }
  return "";
}

string[] tests = {
  "buy and hold 전략",
  "buy gold now",
  "do the analysis",
  "is it a good buy",
  "원/달러 risk",
  "sell 타이밍 잡아줘",
  "THE AAPL 미래 전망"
};
foreach(var t in tests) Console.WriteLine($"{t,-28} -> Symbol={ExtractSymbol(t)}");
