using System;
using System.Collections.Generic;
using System.Linq;
using cAlgo.API;
using cAlgo.API.Internals;

namespace cAlgo.Robots
{
    // ═══════════════════════════════════════════════════════════════════════════
    //  MULTI-STRATEGY-BOT  (strukturierte Neufassung)
    //
    //  Drei unabhängige Strategien in einem Bot:
    //    • HH/LL  – Tageskerzen-Breakout (Trend),    Fenster 00–13 UTC
    //    • ARB    – Asian-Range-Breakout (Trend),    Fenster 07–11 UTC
    //    • RTO    – Return-to-Open (Mean-Reversion),  Fenster 14–18 UTC
    //
    //  Slot-System:  TREND-Slot (HH/LL + ARB teilen sich 1 Trade)
    //                REV-Slot   (RTO, 1 Trade)            → beide parallel.
    //
    //  Einheitliches Management:
    //    • Risiko: RiskPercent % vom AKTUELLEN Guthaben (Compounding)
    //    • Break-even: ab BeRThreshold R → Stop +BeLockPips ins Plus (nur Trend)
    //    • H1-Trailing: ab TrailHour unter letztes H1-Tief/Hoch (nur Trend)
    //    • TP-Trigger-Trail: TP-Level löst engen Trail aus (ALLE Strategien)
    //          Abstand pro Strategie: HhllTpTrailGapPips / ArbTpTrailGapPips / RtoTpTrailGapPips
    //    • Notfall-TP: TP-Level + EmergencyTpPips broker-seitig (fängt Bot-Ausfall)
    //    • EOD-Exit: alle Trades um EodHour UTC schließen
    //
    //  Alle Zeiten UTC (TimeZone = UTC) → standortunabhängig.
    // ═══════════════════════════════════════════════════════════════════════════
    [Robot(TimeZone = TimeZones.UTC, AccessRights = AccessRights.None)]
    public class MultiStrategyBot : Robot
    {
        // ───────────────────────────────────────────────────────────────────────
        //  PARAMETER – Allgemein
        // ───────────────────────────────────────────────────────────────────────
        [Parameter("Risiko % pro Trade", DefaultValue = 1.0, MinValue = 0.1, MaxValue = 5.0, Step = 0.1, Group = "Allgemein")]
        public double RiskPercent { get; set; }

        [Parameter("Symbole (Komma-getrennt)", DefaultValue = "EURUSD,GBPUSD,USDJPY", Group = "Allgemein")]
        public string SymbolList { get; set; }

        [Parameter("Max. offene Trades gesamt (0=Auto)", DefaultValue = 0, MinValue = 0, MaxValue = 10, Group = "Allgemein")]
        public int MaxTotalPositions { get; set; }

        // ───────────────────────────────────────────────────────────────────────
        //  PARAMETER – Strategien an/aus
        // ───────────────────────────────────────────────────────────────────────
        [Parameter("HH/LL aktiv", DefaultValue = true, Group = "Strategien")]
        public bool HhllEnabled { get; set; }

        [Parameter("ARB aktiv", DefaultValue = true, Group = "Strategien")]
        public bool ArbEnabled { get; set; }

        [Parameter("RTO aktiv", DefaultValue = true, Group = "Strategien")]
        public bool RtoEnabled { get; set; }

        // ───────────────────────────────────────────────────────────────────────
        //  PARAMETER – HH/LL
        // ───────────────────────────────────────────────────────────────────────
        [Parameter("HHLL: Stop-Anteil", DefaultValue = 0.5, MinValue = 0.1, MaxValue = 1.0, Step = 0.05, Group = "HH/LL")]
        public double HhllStopFraction { get; set; }

        [Parameter("HHLL: Entry-Start (UTC)", DefaultValue = 0, MinValue = 0, MaxValue = 23, Group = "HH/LL")]
        public int HhllStart { get; set; }

        [Parameter("HHLL: Entry-Ende (UTC)", DefaultValue = 13, MinValue = 0, MaxValue = 23, Group = "HH/LL")]
        public int HhllEnd { get; set; }

        [Parameter("HHLL: TP-Trail Abstand (Pips)", DefaultValue = 1.0, MinValue = 0.5, MaxValue = 30, Step = 0.5, Group = "HH/LL")]
        public double HhllTpTrailGapPips { get; set; }

        // ───────────────────────────────────────────────────────────────────────
        //  PARAMETER – ARB
        // ───────────────────────────────────────────────────────────────────────
        [Parameter("ARB: Asia-Start (UTC)", DefaultValue = 0, MinValue = 0, MaxValue = 23, Group = "ARB")]
        public int ArbAsiaStart { get; set; }

        [Parameter("ARB: Asia-Ende (UTC)", DefaultValue = 6, MinValue = 0, MaxValue = 23, Group = "ARB")]
        public int ArbAsiaEnd { get; set; }

        [Parameter("ARB: London-Start (UTC)", DefaultValue = 7, MinValue = 0, MaxValue = 23, Group = "ARB")]
        public int ArbLonStart { get; set; }

        [Parameter("ARB: London-Ende (UTC)", DefaultValue = 11, MinValue = 0, MaxValue = 23, Group = "ARB")]
        public int ArbLonEnd { get; set; }

        [Parameter("ARB: TP-Trail Abstand (Pips)", DefaultValue = 1.0, MinValue = 0.5, MaxValue = 30, Step = 0.5, Group = "ARB")]
        public double ArbTpTrailGapPips { get; set; }

        // ───────────────────────────────────────────────────────────────────────
        //  PARAMETER – RTO
        // ───────────────────────────────────────────────────────────────────────
        [Parameter("RTO: US-Start (UTC)", DefaultValue = 14, MinValue = 0, MaxValue = 23, Group = "RTO")]
        public int RtoStart { get; set; }

        [Parameter("RTO: US-Ende (UTC)", DefaultValue = 18, MinValue = 0, MaxValue = 23, Group = "RTO")]
        public int RtoEnd { get; set; }

        [Parameter("RTO: Trigger (×ATR)", DefaultValue = 0.7, MinValue = 0.1, MaxValue = 2.0, Step = 0.1, Group = "RTO")]
        public double RtoTriggerAtr { get; set; }

        [Parameter("RTO: TP-Anteil", DefaultValue = 0.5, MinValue = 0.1, MaxValue = 1.0, Step = 0.1, Group = "RTO")]
        public double RtoTpFraction { get; set; }

        [Parameter("RTO: Stop (×ATR)", DefaultValue = 0.5, MinValue = 0.1, MaxValue = 1.5, Step = 0.1, Group = "RTO")]
        public double RtoStopAtr { get; set; }

        [Parameter("RTO: TP-Trigger-Trail Abstand (Pips)", DefaultValue = 1.0, MinValue = 0.5, MaxValue = 30, Step = 0.5, Group = "RTO")]
        public double RtoTpTrailGapPips { get; set; }

        [Parameter("RTO: Symbole (kein Gold!)", DefaultValue = "EURUSD,GBPUSD,USDJPY", Group = "RTO")]
        public string RtoSymbolList { get; set; }

        // ───────────────────────────────────────────────────────────────────────
        //  PARAMETER – Management (Trend + gemeinsam)
        // ───────────────────────────────────────────────────────────────────────
        [Parameter("BE-Trigger nach Std (0=aus)", DefaultValue = 2, MinValue = 0, MaxValue = 12, Group = "Management")]
        public int BeAfterHours { get; set; }

        [Parameter("BE-Trigger R-Schwelle", DefaultValue = 0.7, MinValue = 0.1, MaxValue = 2.0, Step = 0.1, Group = "Management")]
        public double BeRThreshold { get; set; }

        [Parameter("BE Lock-Gewinn (Pips)", DefaultValue = 1.0, MinValue = 0, MaxValue = 50, Step = 0.5, Group = "Management")]
        public double BeLockPips { get; set; }

        [Parameter("Trailing ab Std (0=aus)", DefaultValue = 9, MinValue = 0, MaxValue = 23, Group = "Management")]
        public int TrailHour { get; set; }

        [Parameter("Notfall-TP Abstand (Pips, 0=aus)", DefaultValue = 5.0, MinValue = 0, MaxValue = 50, Step = 0.5, Group = "Management")]
        public double EmergencyTpPips { get; set; }

        [Parameter("EOD-Exit Stunde (UTC)", DefaultValue = 22, MinValue = 0, MaxValue = 23, Group = "Management")]
        public int EodHour { get; set; }

        // ───────────────────────────────────────────────────────────────────────
        //  PARAMETER – Sicherheit
        // ───────────────────────────────────────────────────────────────────────
        [Parameter("Max Verluste/Tag (0=aus)", DefaultValue = 3, MinValue = 0, MaxValue = 20, Group = "Sicherheit")]
        public int MaxLossesPerDay { get; set; }

        [Parameter("Max DrawDown % Stop", DefaultValue = 15.0, MinValue = 0, MaxValue = 50, Step = 1.0, Group = "Sicherheit")]
        public double MaxDrawDownPercent { get; set; }

        [Parameter("Spreadfilter pro Symbol (Pips)", DefaultValue = "EURUSD:0.5,GBPUSD:1.0,USDJPY:3", Group = "Sicherheit")]
        public string SpreadFilterList { get; set; }

        // ───────────────────────────────────────────────────────────────────────
        //  PARAMETER – Broker
        // ───────────────────────────────────────────────────────────────────────
        [Parameter("PipSize Overrides", DefaultValue = "XAUUSD:0.10", Group = "Broker")]
        public string PipSizeOverrides { get; set; }

        // ═══════════════════════════════════════════════════════════════════════
        //  DATENSTRUKTUREN
        // ═══════════════════════════════════════════════════════════════════════
        private const string SLOT_TREND = "TREND";
        private const string SLOT_REV   = "REV";

        // Symbol-spezifische Konfiguration (TP-Multiplikatoren, Stop-Grenzen)
        private class SymCfg
        {
            public string Name;
            public Symbol Symbol;
            public Bars H1;
            public Bars D1;
            public double PipSize;
            public double HhllTpLong, HhllTpShort;  // TP-Multiplikator HH/LL
            public double MinSl, MaxSl;             // Stop-Grenzen (Pips)
            public double ArbTp;                    // TP-Multiplikator ARB
            public double SpreadFilterPips;         // max. Spread für Entry
        }

        // Laufzeit-Zustand einer offenen Position
        private class PosState
        {
            public DateTime EntryTime;
            public double EntryPrice;
            public double InitialSlPips;   // 1R-Referenz
            public bool BeDone;            // Break-even bereits gesetzt?
            public string Slot;
            public string Strat;
            public bool IsTrend;           // true = HH/LL/ARB, false = RTO
            public double TpPrice;         // Ziel-Level (= Trail-Auslöser)
            public bool TpTrailActive;     // TP-Level bereits erreicht?
            public TradeType Dir;
            public double LastStopPrice;   // zuletzt gesetzter Stop (für Exit-Slippage)
            public string CloseReason;     // gesetzt bei gewolltem Close (EOD), sonst null
        }

        private readonly Dictionary<string, SymCfg> _syms = new Dictionary<string, SymCfg>();
        private readonly Dictionary<long, PosState> _posStates = new Dictionary<long, PosState>();
        private readonly Dictionary<string, DateTime> _tradedToday = new Dictionary<string, DateTime>();
        private HashSet<string> _rtoSymbols;
        private int _lossesToday;
        private DateTime _lossDay;
        private double _peakEquity;
        private bool _halted;

        // Standard-Symbol-Konfiguration (TP-L, TP-S, MinSl, MaxSl, ArbTp)
        private static readonly Dictionary<string, (double, double, double, double, double)> Defaults =
            new Dictionary<string, (double, double, double, double, double)>
        {
            { "EURUSD", (1.5, 3.0, 10, 80,  2.0) },
            { "GBPUSD", (1.5, 2.5, 12, 110, 1.5) },
            { "USDJPY", (3.0, 1.5, 10, 130, 2.5) },
        };

        // ═══════════════════════════════════════════════════════════════════════
        //  SETUP
        // ═══════════════════════════════════════════════════════════════════════
        protected override void OnStart()
        {
            if (!ValidateParameters()) { Stop(); return; }

            _rtoSymbols = new HashSet<string>(
                RtoSymbolList.Split(',').Select(s => s.Trim().ToUpper()));
            var spreadMap = ParseKeyValue(SpreadFilterList);
            var pipMap = ParseKeyValue(PipSizeOverrides);

            foreach (var raw in SymbolList.Split(','))
            {
                var name = raw.Trim().ToUpper();
                if (string.IsNullOrEmpty(name) || !Defaults.ContainsKey(name)) continue;

                var sym = Symbols.GetSymbol(name);
                if (sym == null) { Print("Symbol {0} nicht verfügbar – übersprungen.", name); continue; }

                var def = Defaults[name];
                double pipSize = pipMap.ContainsKey(name) ? pipMap[name] : sym.PipSize;

                _syms[name] = new SymCfg
                {
                    Name = name,
                    Symbol = sym,
                    H1 = MarketData.GetBars(TimeFrame.Hour, name),
                    D1 = MarketData.GetBars(TimeFrame.Daily, name),
                    PipSize = pipSize,
                    HhllTpLong = def.Item1, HhllTpShort = def.Item2,
                    MinSl = def.Item3, MaxSl = def.Item4, ArbTp = def.Item5,
                    SpreadFilterPips = spreadMap.ContainsKey(name) ? spreadMap[name] : 3.0
                };
            }

            _peakEquity = Account.Equity;
            _lossDay = Server.Time.Date;
            Print("MultiStrategyBot gestartet | {0} Symbole | Risiko {1}% | UTC",
                  _syms.Count, RiskPercent);
        }

        // Plausibilitätsprüfung der Parameter – verhindert stilles Fehlverhalten
        private bool ValidateParameters()
        {
            var errs = new List<string>();

            if (HhllEnabled && HhllStart >= HhllEnd)
                errs.Add(string.Format("HHLL: Entry-Start ({0}) muss kleiner als Ende ({1}) sein.", HhllStart, HhllEnd));
            if (ArbEnabled && ArbAsiaStart >= ArbAsiaEnd)
                errs.Add(string.Format("ARB: Asia-Start ({0}) muss kleiner als Asia-Ende ({1}) sein.", ArbAsiaStart, ArbAsiaEnd));
            if (ArbEnabled && ArbLonStart >= ArbLonEnd)
                errs.Add(string.Format("ARB: London-Start ({0}) muss kleiner als London-Ende ({1}) sein.", ArbLonStart, ArbLonEnd));
            if (ArbEnabled && ArbLonStart < ArbAsiaEnd)
                errs.Add(string.Format("ARB: London-Start ({0}) liegt im Asia-Fenster (Ende {1}) – Range unvollständig.", ArbLonStart, ArbAsiaEnd));
            if (RtoEnabled && RtoStart >= RtoEnd)
                errs.Add(string.Format("RTO: US-Start ({0}) muss kleiner als US-Ende ({1}) sein.", RtoStart, RtoEnd));

            // EOD muss nach allen Entry-Fenstern liegen, sonst werden Trades sofort geschlossen
            int latestEntry = 0;
            if (HhllEnabled) latestEntry = Math.Max(latestEntry, HhllEnd);
            if (ArbEnabled)  latestEntry = Math.Max(latestEntry, ArbLonEnd);
            if (RtoEnabled)  latestEntry = Math.Max(latestEntry, RtoEnd);
            if (EodHour <= latestEntry)
                errs.Add(string.Format("EOD-Stunde ({0}) liegt im/vor letztem Entry-Fenster (bis {1}) – Trades würden sofort geschlossen.", EodHour, latestEntry));

            if (errs.Count > 0)
            {
                Print("PARAMETER-FEHLER – Bot wird NICHT gestartet:");
                foreach (var e in errs) Print("  • " + e);
                return false;
            }
            return true;
        }

        private Dictionary<string, double> ParseKeyValue(string s)
        {
            var d = new Dictionary<string, double>();
            if (string.IsNullOrEmpty(s)) return d;
            foreach (var part in s.Split(','))
            {
                var kv = part.Split(':');
                if (kv.Length == 2 && double.TryParse(kv[1].Trim(),
                    System.Globalization.NumberStyles.Any,
                    System.Globalization.CultureInfo.InvariantCulture, out double v))
                    d[kv[0].Trim().ToUpper()] = v;
            }
            return d;
        }

        // ═══════════════════════════════════════════════════════════════════════
        //  HAUPTSCHLEIFE
        // ═══════════════════════════════════════════════════════════════════════
        protected override void OnBar()
        {
            if (_halted) return;
            var now = Server.Time;

            // Tageswechsel: Verlustzähler zurücksetzen
            if (now.Date != _lossDay) { _lossDay = now.Date; _lossesToday = 0; }

            // Sicherheits-Stopps
            if (MaxDrawDownPercent > 0)
            {
                _peakEquity = Math.Max(_peakEquity, Account.Equity);
                double ddPct = (_peakEquity - Account.Equity) / _peakEquity * 100.0;
                if (ddPct >= MaxDrawDownPercent)
                {
                    Print("Max DrawDown {0:F1}% erreicht – Bot pausiert.", ddPct);
                    _halted = true;
                    return;
                }
            }

            ManageOpenPositions(now);

            if (MaxLossesPerDay > 0 && _lossesToday >= MaxLossesPerDay) return;

            // Signal-Erkennung je Symbol (1× pro abgeschlossener H1-Bar)
            foreach (var cfg in _syms.Values)
            {
                if (HhllEnabled || ArbEnabled) TryTrendSlot(cfg, now);
                if (RtoEnabled && _rtoSymbols.Contains(cfg.Name)) TryRto(cfg, now);
            }
        }

        // ───────────────────────────────────────────────────────────────────────
        //  TREND-SLOT (HH/LL + ARB konkurrieren um 1 Trade)
        // ───────────────────────────────────────────────────────────────────────
        private void TryTrendSlot(SymCfg cfg, DateTime now)
        {
            if (HasOpenSlot(SLOT_TREND) || !CanOpenNew()) return;
            if (TradedTodayInSlot(cfg.Name, SLOT_TREND, now)) return;

            if (HhllEnabled && TryHHLL(cfg, now)) return;
            if (ArbEnabled) TryARB(cfg, now);
        }

        // HH/LL – Tageskerzen-Breakout
        private bool TryHHLL(SymCfg cfg, DateTime now)
        {
            int h = now.Hour;
            if (h < HhllStart || h >= HhllEnd) return false;
            if (cfg.D1.Count < 2 || cfg.H1.Count < 2) return false;

            double pip = cfg.PipSize;
            double prevHigh = cfg.D1.HighPrices.Last(1);
            double prevLow  = cfg.D1.LowPrices.Last(1);
            double prevClose = cfg.D1.ClosePrices.Last(1);
            double bid = cfg.Symbol.Bid;
            double lastH1Close = cfg.H1.ClosePrices.Last(1);
            double todayHigh = cfg.D1.HighPrices.Last(0);
            double todayLow  = cfg.D1.LowPrices.Last(0);

            // LONG: heutiges höheres Hoch + über Vortagesschluss + H1-Schluss über Vortageshoch
            if (todayHigh > prevHigh && bid > prevClose && lastH1Close > prevHigh)
            {
                double struct_ = (prevHigh - prevLow) / pip;
                if (struct_ < cfg.MinSl) return false;
                if (struct_ > cfg.MaxSl) struct_ = cfg.MaxSl;
                double slPips = Math.Max(struct_ * HhllStopFraction, cfg.MinSl);
                double tpPips = struct_ * cfg.HhllTpLong;
                return OpenTrade(cfg, TradeType.Buy, slPips, tpPips, SLOT_TREND, "HHLL", now);
            }
            // SHORT: spiegelbildlich
            if (todayLow < prevLow && bid < prevClose && lastH1Close < prevLow)
            {
                double struct_ = (prevHigh - prevLow) / pip;
                if (struct_ < cfg.MinSl) return false;
                if (struct_ > cfg.MaxSl) struct_ = cfg.MaxSl;
                double slPips = Math.Max(struct_ * HhllStopFraction, cfg.MinSl);
                double tpPips = struct_ * cfg.HhllTpShort;
                return OpenTrade(cfg, TradeType.Sell, slPips, tpPips, SLOT_TREND, "HHLL", now);
            }
            return false;
        }

        // ARB – Asian-Range-Breakout
        private bool TryARB(SymCfg cfg, DateTime now)
        {
            int h = now.Hour;
            if (h < ArbLonStart || h >= ArbLonEnd) return false;
            if (cfg.H1.Count < 2) return false;

            double pip = cfg.PipSize;
            // Asia-Range aus den H1-Bars des heutigen Asia-Fensters bilden
            double asiaHigh = double.MinValue, asiaLow = double.MaxValue;
            bool found = false;
            for (int i = 1; i < cfg.H1.Count && i < 48; i++)
            {
                var t = cfg.H1.OpenTimes.Last(i);
                if (t.Date != now.Date) break;
                if (t.Hour >= ArbAsiaStart && t.Hour < ArbAsiaEnd)
                {
                    asiaHigh = Math.Max(asiaHigh, cfg.H1.HighPrices.Last(i));
                    asiaLow  = Math.Min(asiaLow,  cfg.H1.LowPrices.Last(i));
                    found = true;
                }
            }
            if (!found) return false;

            double rangePips = (asiaHigh - asiaLow) / pip;
            if (rangePips < cfg.MinSl) return false;

            double lastClose = cfg.H1.ClosePrices.Last(1);
            // Breakout mit H1-Schluss-Bestätigung
            if (lastClose > asiaHigh)
            {
                double slPips = rangePips;
                double tpPips = rangePips * cfg.ArbTp;
                return OpenTrade(cfg, TradeType.Buy, slPips, tpPips, SLOT_TREND, "ARB", now);
            }
            if (lastClose < asiaLow)
            {
                double slPips = rangePips;
                double tpPips = rangePips * cfg.ArbTp;
                return OpenTrade(cfg, TradeType.Sell, slPips, tpPips, SLOT_TREND, "ARB", now);
            }
            return false;
        }

        // ───────────────────────────────────────────────────────────────────────
        //  REV-SLOT (RTO – Return-to-Open, Mean-Reversion)
        // ───────────────────────────────────────────────────────────────────────
        private void TryRto(SymCfg cfg, DateTime now)
        {
            if (HasOpenSlot(SLOT_REV) || !CanOpenNew()) return;
            if (TradedTodayInSlot(cfg.Name, SLOT_REV, now)) return;

            int h = now.Hour;
            if (h < RtoStart || h >= RtoEnd) return;
            if (cfg.H1.Count < 2 || cfg.D1.Count < 21) return;

            double pip = cfg.PipSize;
            // ATR = Ø Tagesspanne der letzten 20 Tage
            double atrSum = 0;
            for (int i = 1; i <= 20; i++)
                atrSum += (cfg.D1.HighPrices.Last(i) - cfg.D1.LowPrices.Last(i)) / pip;
            double atr = atrSum / 20.0;
            if (atr < 5) return;

            // Tages-Open = Open der ersten H1-Bar des US-Fensters
            double dayOpen = FindUsOpen(cfg, now);
            if (double.IsNaN(dayOpen)) return;

            double trigPips = RtoTriggerAtr * atr;
            double bid = cfg.Symbol.Bid, ask = cfg.Symbol.Ask;
            double upExt = (bid - dayOpen) / pip;
            double dnExt = (dayOpen - bid) / pip;

            double slPips = RtoStopAtr * atr;
            double tpPips = trigPips * RtoTpFraction;

            // Überdehnung nach oben → SHORT (Fade)
            if (upExt >= trigPips)
                OpenTrade(cfg, TradeType.Sell, slPips, tpPips, SLOT_REV, "RTO", now);
            // Überdehnung nach unten → LONG (Fade)
            else if (dnExt >= trigPips)
                OpenTrade(cfg, TradeType.Buy, slPips, tpPips, SLOT_REV, "RTO", now);
        }

        private double FindUsOpen(SymCfg cfg, DateTime now)
        {
            for (int i = 1; i < cfg.H1.Count && i < 48; i++)
            {
                var t = cfg.H1.OpenTimes.Last(i);
                if (t.Date != now.Date) break;
                if (t.Hour == RtoStart) return cfg.H1.OpenPrices.Last(i);
            }
            return double.NaN;
        }

        // ═══════════════════════════════════════════════════════════════════════
        //  ORDER-AUSFÜHRUNG
        // ═══════════════════════════════════════════════════════════════════════
        private bool OpenTrade(SymCfg cfg, TradeType dir, double slPips, double tpPips,
                               string slot, string strat, DateTime now)
        {
            // Spread-Filter
            double spreadPips = (cfg.Symbol.Ask - cfg.Symbol.Bid) / cfg.PipSize;
            if (spreadPips > cfg.SpreadFilterPips)
            {
                Print("{0} {1}: Spread {2:F1}p > Filter {3:F1}p – kein Trade.",
                      strat, cfg.Name, spreadPips, cfg.SpreadFilterPips);
                return false;
            }

            double volume = CalcVolume(cfg, slPips);
            if (volume <= 0) return false;

            // cTrader rechnet SL/TP in "Broker-Pips" – Umrechnungsfaktor
            double pipMult = cfg.PipSize / cfg.Symbol.PipSize;
            double slBroker = slPips * pipMult;

            bool isTrend = (slot == SLOT_TREND);
            string label = string.Format("MSB_{0}_{1}_{2}", slot, strat, cfg.Name);

            // KEIN hartes TP – nur Notfall-TP (TP-Level + Puffer) broker-seitig.
            double emTpBroker = (EmergencyTpPips > 0) ? (tpPips + EmergencyTpPips) * pipMult : 0;
            double? emTp = (emTpBroker > 0) ? (double?)emTpBroker : null;

            // Referenzpreis VOR Ausführung → Einstiegs-Slippage messbar
            double refPrice = (dir == TradeType.Buy) ? cfg.Symbol.Ask : cfg.Symbol.Bid;

            var result = ExecuteMarketOrder(dir, cfg.Name, volume, label, slBroker, emTp);
            if (result == null || !result.IsSuccessful || result.Position == null)
            {
                Print("{0} {1} {2}: Order fehlgeschlagen ({3})", strat, cfg.Name, dir,
                      result == null ? "null" : result.Error.ToString());
                return false;
            }

            var pos = result.Position;
            double tpPrice = (dir == TradeType.Buy)
                ? pos.EntryPrice + tpPips * cfg.PipSize
                : pos.EntryPrice - tpPips * cfg.PipSize;

            double slPrice = pos.StopLoss ?? 0.0;
            double entrySlip = (dir == TradeType.Buy)
                ? (pos.EntryPrice - refPrice) / cfg.PipSize   // > 0 = schlechter gefüllt
                : (refPrice - pos.EntryPrice) / cfg.PipSize;

            _posStates[pos.Id] = new PosState
            {
                EntryTime = now,
                EntryPrice = pos.EntryPrice,
                InitialSlPips = slPips,
                BeDone = false,
                Slot = slot,
                Strat = strat,
                IsTrend = isTrend,
                TpPrice = tpPrice,
                TpTrailActive = false,
                Dir = dir,
                LastStopPrice = slPrice,
                CloseReason = null
            };
            MarkTradedToday(cfg.Name, slot, now);
            Journal("ENTRY", cfg.Name, strat, pos.Id, string.Format(
                "dir={0}|entry={1:F5}|vol={2}|sl={3:F5}|trig={4:F5}|spread={5:F2}|slipEntry={6:F2}",
                dir, pos.EntryPrice, volume, slPrice, tpPrice, spreadPips, entrySlip));
            return true;
        }

        // Positionsgröße: RiskPercent % vom AKTUELLEN Guthaben (Compounding)
        private double CalcVolume(SymCfg cfg, double slPips)
        {
            double riskAmount = Account.Balance * (RiskPercent / 100.0);
            double valuePerPipPerUnit = cfg.Symbol.PipValue;
            double valuePerPipPerLot = valuePerPipPerUnit * cfg.Symbol.LotSize;
            if (valuePerPipPerLot <= 0 || slPips <= 0) return 0;

            double slBroker = slPips * (cfg.PipSize / cfg.Symbol.PipSize);
            double lots = riskAmount / (slBroker * valuePerPipPerLot);
            double units = lots * cfg.Symbol.LotSize;
            units = cfg.Symbol.NormalizeVolumeInUnits(units, RoundingMode.Down);
            if (units < cfg.Symbol.VolumeInUnitsMin) return 0;  // Stop zu groß für 1% → kein Trade
            return units;
        }

        // ═══════════════════════════════════════════════════════════════════════
        //  POSITIONS-MANAGEMENT
        //    BE + H1-Trail: nur Trend · TP-Trigger-Trail: alle · EOD: alle
        // ═══════════════════════════════════════════════════════════════════════
        private void ManageOpenPositions(DateTime now)
        {
            foreach (var pos in Positions)
            {
                if (!pos.Label.StartsWith("MSB_")) continue;
                if (!_posStates.TryGetValue(pos.Id, out var st)) continue;
                var cfg = _syms.Values.FirstOrDefault(c => c.Name == pos.SymbolName);
                if (cfg == null) continue;

                double pip = cfg.PipSize;
                double hrs = (now - st.EntryTime).TotalHours;

                // ── EOD-Exit (alle Strategien) ──
                if (now.Hour >= EodHour) { st.CloseReason = "EOD"; ClosePosition(pos); continue; }

                // ── BE + H1-Trail (nur Trend) ──
                if (st.IsTrend)
                {
                    double cR = CurrentR(cfg, pos, st);

                    // Break-even: Stop +BeLockPips ins Plus (nicht exakt Entry)
                    if (BeAfterHours > 0 && !st.BeDone && hrs >= BeAfterHours && cR >= BeRThreshold)
                    {
                        double lock_ = BeLockPips * pip;
                        if (pos.TradeType == TradeType.Buy)
                        {
                            double target = st.EntryPrice + lock_;
                            if (target > (pos.StopLoss ?? double.MinValue) && target < cfg.Symbol.Bid)
                            {
                                double oldSl = pos.StopLoss ?? 0.0;
                                SetStop(pos, target); st.BeDone = true; st.LastStopPrice = target;
                                Journal("BE", pos.SymbolName, st.Strat, pos.Id, string.Format(
                                    "oldSL={0:F5}|newSL={1:F5}|R={2:F2}|lockPips={3:F1}", oldSl, target, cR, BeLockPips));
                            }
                        }
                        else
                        {
                            double target = st.EntryPrice - lock_;
                            if (target < (pos.StopLoss ?? double.MaxValue) && target > cfg.Symbol.Ask)
                            {
                                double oldSl = pos.StopLoss ?? 0.0;
                                SetStop(pos, target); st.BeDone = true; st.LastStopPrice = target;
                                Journal("BE", pos.SymbolName, st.Strat, pos.Id, string.Format(
                                    "oldSL={0:F5}|newSL={1:F5}|R={2:F2}|lockPips={3:F1}", oldSl, target, cR, BeLockPips));
                            }
                        }
                    }

                    // H1-Trailing: Stop unter letztes H1-Tief / über letztes H1-Hoch
                    if (TrailHour > 0 && hrs >= TrailHour && cfg.H1.Count >= 2)
                    {
                        double buf = pip;
                        if (pos.TradeType == TradeType.Buy)
                        {
                            double trail = cfg.H1.LowPrices.Last(1);
                            if (trail > st.EntryPrice && trail < cfg.Symbol.Bid - buf
                                && trail > (pos.StopLoss ?? double.MinValue))
                            {
                                double oldSl = pos.StopLoss ?? 0.0;
                                SetStop(pos, trail); st.LastStopPrice = trail;
                                Journal("H1TRAIL", pos.SymbolName, st.Strat, pos.Id, string.Format(
                                    "oldSL={0:F5}|newSL={1:F5}|R={2:F2}|lockPips={3:F1}",
                                    oldSl, trail, cR, (trail - st.EntryPrice) / pip));
                            }
                        }
                        else
                        {
                            double trail = cfg.H1.HighPrices.Last(1);
                            if (trail < st.EntryPrice && trail > cfg.Symbol.Ask + buf
                                && trail < (pos.StopLoss ?? double.MaxValue))
                            {
                                double oldSl = pos.StopLoss ?? 0.0;
                                SetStop(pos, trail); st.LastStopPrice = trail;
                                Journal("H1TRAIL", pos.SymbolName, st.Strat, pos.Id, string.Format(
                                    "oldSL={0:F5}|newSL={1:F5}|R={2:F2}|lockPips={3:F1}",
                                    oldSl, trail, cR, (st.EntryPrice - trail) / pip));
                            }
                        }
                    }
                }

                // ── TP-Trigger-Trail (alle Strategien) ──
                double gapPips = st.Strat == "HHLL" ? HhllTpTrailGapPips
                               : st.Strat == "ARB"  ? ArbTpTrailGapPips
                               :                      RtoTpTrailGapPips;
                if (st.TpPrice > 0 && gapPips > 0)
                {
                    double gap = gapPips * pip;
                    if (pos.TradeType == TradeType.Buy)
                    {
                        if (!st.TpTrailActive && cfg.Symbol.Bid >= st.TpPrice)
                        {
                            st.TpTrailActive = true;
                            if (pos.TakeProfit.HasValue) { try { pos.ModifyTakeProfitPrice((double?)null); } catch {} }
                            Journal("TPTRAIL_START", pos.SymbolName, st.Strat, pos.Id, string.Format(
                                "trigPrice={0:F5}|gapPips={1:F1}", st.TpPrice, gapPips));
                        }
                        if (st.TpTrailActive)
                        {
                            double newSl = cfg.Symbol.Bid - gap;
                            if (newSl > (pos.StopLoss ?? double.MinValue) && newSl < cfg.Symbol.Bid)
                            { SetStop(pos, newSl); st.LastStopPrice = newSl; }
                        }
                    }
                    else
                    {
                        if (!st.TpTrailActive && cfg.Symbol.Ask <= st.TpPrice)
                        {
                            st.TpTrailActive = true;
                            if (pos.TakeProfit.HasValue) { try { pos.ModifyTakeProfitPrice((double?)null); } catch {} }
                            Journal("TPTRAIL_START", pos.SymbolName, st.Strat, pos.Id, string.Format(
                                "trigPrice={0:F5}|gapPips={1:F1}", st.TpPrice, gapPips));
                        }
                        if (st.TpTrailActive)
                        {
                            double newSl = cfg.Symbol.Ask + gap;
                            if (newSl < (pos.StopLoss ?? double.MaxValue) && newSl > cfg.Symbol.Ask)
                            { SetStop(pos, newSl); st.LastStopPrice = newSl; }
                        }
                    }
                }
            }
        }

        private double CurrentR(SymCfg cfg, Position pos, PosState st)
        {
            if (st.InitialSlPips <= 0) return 0;
            double pip = cfg.PipSize;
            double profitPips = (pos.TradeType == TradeType.Buy)
                ? (cfg.Symbol.Bid - st.EntryPrice) / pip
                : (st.EntryPrice - cfg.Symbol.Ask) / pip;
            return profitPips / st.InitialSlPips;
        }

        private void SetStop(Position pos, double price)
        {
            try { pos.ModifyStopLossPrice(price); }
            catch (Exception e) { Print("StopLoss-Fehler {0}: {1}", pos.SymbolName, e.Message); }
        }

        // ═══════════════════════════════════════════════════════════════════════
        //  POSITION GESCHLOSSEN → Verlustzähler + State aufräumen
        // ═══════════════════════════════════════════════════════════════════════
        protected override void OnPositionClosed(Position pos)
        {
            if (pos.Label != null && pos.Label.StartsWith("MSB_"))
            {
                _posStates.TryGetValue(pos.Id, out var st);
                var cfg = _syms.Values.FirstOrDefault(c => c.Name == pos.SymbolName);
                double pip = cfg != null ? cfg.PipSize : 0.0001;

                // Grund bestimmen (OnPositionClosed liefert keinen Grund mit)
                string reason;
                if (st != null && st.CloseReason != null)   reason = st.CloseReason;   // EOD
                else if (st != null && st.TpTrailActive)     reason = "TPTRAIL";
                else if (pos.GrossProfit >= 0)               reason = "EMERGENCY_TP";
                else                                          reason = "SL";

                double closePrice = FindClosePrice(pos.Id);
                double spreadExit = cfg != null ? (cfg.Symbol.Ask - cfg.Symbol.Bid) / pip : 0;

                // Exit-Slippage = Differenz zuletzt gesetzter Stop ↔ tatsächliche Füllung
                // (nur bei Stop-/Trail-Close aussagekräftig); > 0 = nachteilig gefüllt
                double slipExit = 0;
                if (st != null && st.LastStopPrice > 0 && closePrice > 0
                    && (reason == "SL" || reason == "TPTRAIL"))
                {
                    slipExit = (pos.TradeType == TradeType.Buy)
                        ? (st.LastStopPrice - closePrice) / pip
                        : (closePrice - st.LastStopPrice) / pip;
                }

                Journal("CLOSE", pos.SymbolName, st != null ? st.Strat : "?", pos.Id, string.Format(
                    "reason={0}|close={1:F5}|pnl={2:F2}|pips={3:F1}|spread={4:F2}|slipExit={5:F2}",
                    reason, closePrice, pos.GrossProfit, pos.Pips, spreadExit, slipExit));

                if (pos.GrossProfit < 0) _lossesToday++;
            }
            if (_posStates.ContainsKey(pos.Id)) _posStates.Remove(pos.Id);
        }

        // Schließkurs aus der Handels-History (für Exit-Slippage)
        private double FindClosePrice(long posId)
        {
            var t = History.LastOrDefault(h => h.PositionId == posId);
            return t != null ? t.ClosingPrice : 0.0;
        }

        // Einheitliche, exportierbare Journal-Zeile (Pipe-getrennt → CSV-tauglich)
        private void Journal(string ev, string sym, string strat, long posId, string fields)
        {
            Print("JRNL|{0:yyyy-MM-dd HH:mm:ss}|{1}|{2}|{3}|{4}|{5}",
                  Server.Time, ev, sym, strat, posId, fields);
        }

        // ═══════════════════════════════════════════════════════════════════════
        //  SLOT- & HILFSFUNKTIONEN
        // ═══════════════════════════════════════════════════════════════════════
        private bool HasOpenSlot(string slot)
        {
            foreach (var pos in Positions)
                if (pos.Label != null && pos.Label.StartsWith("MSB_" + slot)) return true;
            return false;
        }

        private int CountBotPositions()
        {
            int n = 0;
            foreach (var pos in Positions)
                if (pos.Label != null && pos.Label.StartsWith("MSB_")) n++;
            return n;
        }

        private bool CanOpenNew()
        {
            int max = MaxTotalPositions > 0 ? MaxTotalPositions : 2; // Auto = TREND + REV
            return CountBotPositions() < max;
        }

        private void MarkTradedToday(string sym, string slot, DateTime now)
        {
            _tradedToday[sym + "_" + slot] = now.Date;
        }

        private bool TradedTodayInSlot(string sym, string slot, DateTime now)
        {
            return _tradedToday.TryGetValue(sym + "_" + slot, out var d) && d == now.Date;
        }
    }
}
