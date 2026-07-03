# Browser-Reader — Smoke-Test (CDP)

> **Verified 2026-07-03** (Edge 149.0.4022.98, CDP 1.3, BrowserAppReader Iter. 3)
> **Spec:** `specs/0004-app-reader.md` (CDP opt-in)
> **Risikofreier Setup:** keiner — startet eine lokale Edge-Instanz auf Port 9222

Manueller End-to-End-Test für den Browser-Reader mit aktiviertem
Chrome-DevTools-Protocol-Pfad. Voraussetzung: Microsoft Edge installiert,
dotnet-Build der Solution vorhanden.

## 1. Browser-Instanz vorbereiten

Browser **muss** mit aktiviertem Debug-Port gestartet werden. Wenn bereits
eine Edge-Session ohne dieses Flag läuft, wird diese **nicht** getroffen
— also vorher alle Edge-Fenster schließen oder mit eigenem `--user-data-dir`
arbeiten, damit der CDP-Browser isoliert ist.

```powershell
$edge = "C:\Program Files (x86)\Microsoft\Edge\Application\msedge.exe"
Get-Process msedge -ErrorAction SilentlyContinue | Stop-Process -Force

Start-Process -FilePath $edge `
  -ArgumentList @(
    "--remote-debugging-port=9222",
    "--remote-allow-origins=*",
    "https://www.heise.de/",         # gewünschte Test-URL
    "--no-first-run",
    "--no-default-browser-check",
    "--user-data-dir=C:\Users\Admin\.openclaw\workspace\temp\edge-cdp-profile"
  ) -WindowStyle Normal -PassThru
```

## 2. CDP prüfen

```powershell
Invoke-WebRequest -Uri "http://127.0.0.1:9222/json/version" -UseBasicParsing
# erwartet: Edg/... mit Protocol-Version: 1.3

(Invoke-WebRequest -Uri "http://127.0.0.1:9222/json" -UseBasicParsing).Content `
  | ConvertFrom-Json | Where-Object { $_.type -eq "page" } `
  | Format-Table url, title, webSocketDebuggerUrl -AutoSize
```

Page-Target sollte nach ~3–8 s die konfigurierte URL listen.

## 3. CDP-Config aktivieren

`appReader.browser.cdp.enabled` steht per Default auf `false`. Für den
Smoke-Test eigene Config anlegen:

```json
{
  "appReader": {
    "browser": {
      "maxTextLengthKB": 200,
      "cdp": {
        "enabled": true,
        "endpoint": "http://127.0.0.1:9222",
        "timeoutMs": 5000
      }
    }
  }
}
```

Speichern unter z. B. `temp/ai-recall-cdp-smoke.json`.

## 4. Capture durchführen

Edge-Fenster **muss** im Foreground sein. Sicherheits-Halber vorher
explizit nach vorn holen:

```powershell
Add-Type @"
using System;
using System.Runtime.InteropServices;
public class W { [DllImport("user32.dll")] public static extern bool SetForegroundWindow(IntPtr h); }
"@
$e = Get-Process msedge | Where-Object MainWindowHandle | Select-Object -First 1
[W]::SetForegroundWindow([IntPtr]::new($e.MainWindowHandle.ToInt64())) | Out-Null
```

Dann:

```powershell
cd projects/ai-recall/src/AiRecall.Cli/bin/Debug/net8.0-windows
./AiRecall.Cli.exe active-window --config "C:\Users\Admin\.openclaw\workspace\temp\ai-recall-cdp-smoke.json"
```

Erwartete Ausgabe (Auszug):

```
Active window: msedge (PID ...) - "heise online - ..."
[INF] Loaded AppReader: Browser (UIA; CDP opt-in via config) (...)
[INF] AppReader Browser ... produced content for msedge/... -> ...content.md
Capture written:
  ...
  App-Reader:  ...\195441-422-...content.md  (Browser (...), kind=url)
```

Die Datei-Endung `.content.md` zeigt, dass der BrowserAppReader geliefert hat.

## 5. Erfolgskriterien

- `appReader=Browser (UIA; CDP opt-in via config)` in der Capture-Log-Zeile
- Im `.content.md` Frontmatter:
  - `kind: "url"`
  - `contentSource: "cdp"` (nicht `"uia-text"` und nicht `"none"`)
  - `url: "<die echte URL>"`
  - `tabTitle: ...` ohne Browser-Suffix (z. B. ohne „ - Microsoft Edge")
- Body enthält strukturiertes Markdown: Headings (`####`), Links
  (`[Label](url)`), Listen — _nicht_ reines HTML.
- Screenshot und `.md` Capture liegen im `capture/<Datum>/msedge/`-Ordner.

## 6. Aufräumen

```powershell
Get-Process msedge | Stop-Process -Force
Remove-Item "C:\Users\Admin\.openclaw\workspace\temp\edge-cdp-profile" -Recurse -Force
Remove-Item "C:\Users\Admin\.openclaw\workspace\temp\ai-recall-cdp-smoke.json"
Get-ChildItem "projects\ai-recall\src\AiRecall.Cli\bin\Debug\net8.0-windows\capture" `
  -Recurse | Remove-Item -Recurse -Force     # capture/ ist gitignored
```

## 7. Beobachtete Werte / Heuristiken

- **Roundtrip-Zeit:** ≈ 300–400 ms für die CDP-Operation (HTTP `/json` +
  WebSocket `Runtime.evaluate`).
- **Maximale Roh-HTML-Größe:** auf `heise.de`-Startseite ~880 KB vor
  `body.innerHTML`, ~515 KB danach, ~457 KB nach Strip-Pass inkl.
  inline-SVG-Ersetzung. Wird durch `maxTextLengthKB` gekappt (Default
  50, für Smoke-Test 200).
- **Aktiv ohne CDP-Server:** BrowserAppReader fällt lautlos auf UIA
  zurück (`contentSource = "none"` wenn weder CDP noch UIA liefern),
  siehe `Read_CdpEnabledButNoServer_FallsBackGracefully`.
- **Inline-SVG-Data-URLs** in `<img src="data:image/svg+xml;base64,...">`
  werden vom Strip-Pass auf den Marker `src="(inline-svg)"` gekürzt
  (auf `heise.de` typisch 20–40 Vorkommen pro Capture).

## 8. Bekannte Limitierungen

- **Andere Data-URLs (PNG/JPEG/WebP):** Nur SVG-Data-URLs werden
  ersetzt. PNG/JPEG-Base64 in `src=` (selten bei reinen Nachrichten-
  Seiten, häufiger bei Apps mit lokalisierten Icons) bleiben im MD.
  Bei Bedarf Regex erweitern auf `data:image/(png|jpeg|webp)`.
- **Erste page-Target-Filterung:** Wir nehmen das erste `type=="page"`
  Target, nicht zwingend das sichtbare Tab. Bei mehreren offenen Tabs
  vorher prüfen, dass das gewünschte zuerst gelistet wird.
- **Kein Headless:** `--headless` mit CDP ist möglich, wurde aber nicht
  getestet. Smoke-Test ist explizit für eine sichtbare Edge-Session.
