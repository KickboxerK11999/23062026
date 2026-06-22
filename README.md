# MultiStrategyBot

Ein cTrader-cBot (C#/cAlgo), der drei unabhängige Strategien parallel handelt – mit einheitlichem Risiko- und Exit-Management, Sicherheitsfiltern und strukturiertem Journal-Logging. Alle Zeiten in UTC (standortunabhängig).

## Strategien

- **HH/LL** – Tageskerzen-Breakout (Trend), Entry-Fenster 00–13 UTC. Long bei höherem Tageshoch + Kurs über Vortagesschluss + H1-Schluss über Vortageshoch (Short spiegelbildlich). Stop = Anteil der Vortagesspanne, TP-Level = Spanne × symbolspezifischer Faktor.
- **ARB** – Asian-Range-Breakout (Trend), Asia-Range 00–06 UTC, London-Entry 07–11 UTC. H1-Schluss-Breakout der Asia-Range. Stop = Range, TP = Range × Faktor.
- **RTO** – Return-to-Open (Mean-Reversion), US-Fenster 14–18 UTC. Misst Überdehnung gegenüber dem US-Open in ×ATR (20-Tage-Spanne) und handelt zurück zum Open.

## Slot-System

- **TREND-Slot** (HH/LL und ARB teilen sich 1 Trade) und **REV-Slot** (RTO, 1 Trade) → max. 2 Trades parallel (Auto).
- Slots gelten portfolioweit (nicht pro Symbol).
- Tages-Guard pro Symbol und Slot: max. ein Trade je Symbol/Slot/Tag.

## Risiko & Positionsgröße

- Risiko in % vom **aktuellen Guthaben** pro Trade (Compounding), Default 1 %.
- Volumen automatisch normalisiert; Trade entfällt, wenn der Stop für das Mindestvolumen zu groß ist.

## Exit- & TP-Management

- **Kein harter TP** – das TP-Level wirkt als Trigger und löst einen engen Pip-Trail aus (alle Strategien).
- **Trail-Abstand pro Strategie einstellbar** (HHLL / ARB / RTO), Default je 1 Pip.
- **Notfall-TP** broker-seitig (TP-Level + Puffer) als Absicherung gegen Bot-Ausfall; wird beim Trail-Start entfernt.
- **Break-even** (nur Trend): nach X Stunden und ab R-Schwelle Stop um feste Pips ins Plus sperren.
- **H1-Trailing** (nur Trend): ab definierter Stunde Stop unter letztes H1-Tief / über letztes H1-Hoch.
- **EOD-Exit**: alle Positionen zur EOD-Stunde (UTC) schließen.

## Sicherheit

- **Spreadfilter** pro Symbol – Entry blockiert, wenn Spread zu hoch.
- **Max. Verluste/Tag** → keine neuen Entries mehr an dem Tag.
- **Max. DrawDown %** → Bot pausiert komplett.
- **Parameter-Validierung** beim Start (Zeitfenster-Logik, EOD nach allen Entry-Fenstern) → startet bei ungültiger Konfiguration nicht.

## Journal / Logging

- Strukturierte, Pipe-getrennte Zeilen (`JRNL|...`, CSV-tauglich) im cTrader-Log.
- Ereignisse: **ENTRY**, **BE**, **H1TRAIL**, **TPTRAIL_START**, **CLOSE**.
- ENTRY protokolliert Spread und Einstiegs-Slippage; CLOSE protokolliert Grund, Schließkurs, P/L, Pips, Exit-Spread und Exit-Slippage.
- Der Pip-Trail erzeugt genau zwei Einträge (Start + Close), keine Zwischen-Nachzüge.
- Slippage-Konvention: > 0 = nachteilig gefüllt, < 0 = besser.

## Technisch

- Plattform: cTrader / cAlgo, `AccessRights.None`, `TimeZone = UTC`.
- Läuft auf H1-Bars (`OnBar`).
- Symbole konfigurierbar; Defaults: EURUSD, GBPUSD, USDJPY.

## Bekannte Einschränkungen

- **Restart-Recovery fehlt**: In-Memory-Zustand (offene Positionen, Tages-Guard, Verlustzähler) geht bei einem Cloud-Neustart verloren – vor dem Neustart geöffnete Trades werden danach nicht weiter gemanagt, der Tages-Guard ist zurückgesetzt.
- **XAUUSD** ist nicht in den Standard-Symbolen enthalten und wird derzeit übersprungen.
- Slots sind portfolioweit, nicht pro Symbol – damit max. 1 Trend- und 1 RTO-Trade über alle Symbole gleichzeitig.
