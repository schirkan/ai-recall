# 0016 — First-Run Settings-Dialog

> **Status:** ✅ **abgeschlossen (2026-07-15)** — `AppSettings.FirstRun` + `UserConfigLocator.LoadOrDefault(out bool)` + `TrayAppContext.MaybeOfferFirstRunSettings(...)`, 5 neue Tests grün.
> **Implements:** First-Run-Dialog wird beim ersten Start der TrayApp automatisch modal angezeigt (vor dem Tessdata-Dialog); per `App.FirstRun = false` deaktivierbar.
> **Owner:** Martin
> **Abhängig von:** Spec 0009 (Settings-Dialog) — erledigt, Spec 0012 (First-Run-Tessdata-Dialog) — erledigt

## Ziel

Wenn die TrayApp zum ersten Mal startet und **noch keine User-Config** unter
`%APPDATA%/AiRecall/config.json` existiert, soll sie automatisch den
Settings-Dialog modal anzeigen, damit der User die wichtigsten Werte (App-Reader-Engine,
tessdata-Sprachen, Audio, Trigger, …) prüfen und ggf. anpassen kann, bevor die
Pipeline produktiv läuft.

Aktuell startet die TrayApp bei fehlender Config mit den `AppConfig`-Defaults
(Tessdata-Download-Dialog kommt nur, wenn die Defaults zufällig
`ocr.engine = "tesseract"` treffen) — der User merkt nichts von den Defaults
und wundert sich ggf. später, warum etwas nicht so läuft wie erwartet.

## Trigger

`TrayAppContext` ruft beim Startup nach dem Config-Load einen neuen
`MaybeOfferFirstRunSettings()`-Hook auf. Auslöser:

1. `UserConfigLocator.LoadOrDefault(out var loadedFromUserFile)` liefert
   `loadedFromUserFile == false` — d. h. es gab **keine** User-Config-Datei
   (nicht-existent **oder** malformed → in beiden Fällen „frischer Start"-Semantik).
2. `_config.App.FirstRun == true` (Default; siehe §Konfiguration).
3. Beide Bedingungen müssen erfüllt sein.

Wenn `App.FirstRun == false` → Dialog wird **nicht** angezeigt, auch wenn
keine User-Config existiert. User kann das Setting später in den Settings
selbst auf `false` setzen, um den Dialog explizit zu unterdrücken
(Use-Case: stille Erstinstallation via Deployment-Script).

## UI

Der bestehende `SettingsDialog` wird **wiederverwendet** — keine neue
Dialog-Klasse. Aufruf erfolgt via `dialog.ShowDialog()` mit Owner = TrayApp-Hauptfenster
(falls bereits vorhanden; sonst Owner = null wie beim Tessdata-Dialog).

```text
┌─ AiRecall — Erste Schritte ───────────────────────────────────┐
│                                                              │
│  Willkommen bei AiRecall!                                    │
│  Es wurde noch keine Benutzer-Konfiguration gefunden.        │
│  Bitte prüfe die wichtigsten Einstellungen.                 │
│                                                              │
│  ┌────────────────────────────────────────────────────────┐  │
│  │  [Settings-Dialog PropertyGrid — alle Werte]           │  │
│  │  - Trigger-Pipeline                                    │  │
│  │  - Audio (Privacy-First)                               │  │
│  │  - OCR (Tesseract/Quick-OCR)                           │  │
│  │  - App-Reader (Browser, Teams, Outlook, …)             │  │
│  └────────────────────────────────────────────────────────┘  │
│                                                              │
│  Tipp: „App.FirstRun" unten kann auf false gesetzt werden,    │
│  um diesen Dialog beim nächsten Start zu unterdrücken.       │
│                                                              │
│              [ Speichern ]  [ Überspringen ]                  │
└──────────────────────────────────────────────────────────────┘
```

### Button-Verhalten

| Button | Wirkung |
|---|---|
| **Speichern** | `SettingsDialog.OnSave` ruft `ApplyConfig(newConfig)` auf → Hot-Reload via `TriggerSupervisor.Restart`. Beim nächsten Start ist `loadedFromUserFile == true` → Dialog erscheint nicht mehr. |
| **Überspringen** | Dialog schließen, Config bleibt auf `AppConfig`-Defaults. Beim nächsten Start ist weiterhin `loadedFromUserFile == false` → Dialog erscheint erneut (User hatte keine Chance zu speichern). User kann später jederzeit über das Tray-Menu „Settings" öffnen. |

## Konfiguration

Neue Property in `AppConfig`:

```csharp
public sealed class AppConfig
{
    // ... bestehende Felder
    public AppSettings App { get; set; } = new();
    // ...
}

public sealed class AppSettings
{
    /// <summary>
    /// Erste-Schritte-Dialog beim ersten Start anzeigen, wenn keine
    /// User-Config existiert (Spec 0016).
    /// </summary>
    public bool FirstRun { get; set; } = true;
}
```

`default-config.json` enthält `app.firstRun: true` (wird durch
`ConfigSerializer` automatisch aus den Property-Defaults erzeugt).

## API-Änderung

**`src\AiRecall.Trigger\UserConfigLocator.cs`**:

```csharp
// Neue Overload — gibt zusätzlich zurück, ob aus User-File geladen wurde.
public static AppConfig LoadOrDefault(out bool loadedFromUserFile, Action<string>? logger = null)
{
    var path = GetUserConfigPath();
    if (!File.Exists(path))
    {
        logger?.Invoke($"User config not found at {path}, using defaults");
        loadedFromUserFile = false;
        return new AppConfig();
    }
    try
    {
        var cfg = ConfigLoader.Load(path);
        loadedFromUserFile = true;
        return cfg;
    }
    catch (Exception ex)
    {
        logger?.Invoke($"User config at {path} is malformed: {ex.Message}, using defaults");
        loadedFromUserFile = false;  // behandelt malformed wie „noch nie gespeichert"
        return new AppConfig();
    }
}

// Bestehender Aufruf bleibt kompatibel:
public static AppConfig LoadOrDefault(Action<string>? logger = null)
{
    var cfg = LoadOrDefault(out _, logger);
    return cfg;
}
```

**`src\AiRecall.TrayApp\TrayAppContext.cs`**:

```csharp
// Konstruktor (nach _config = ...):
var configPath = UserConfigLocator.GetUserConfigPath();
var loadedFromUserFile = File.Exists(configPath); // schneller Pre-Check
// (eigentliche Truth-Quelle: UserConfigLocator.LoadOrDefault(out var l) → l)

var wasFirstRun = !loadedFromUserFile && _config.App.FirstRun;
if (wasFirstRun)
{
    Log.Information("First run detected (no user config + App.FirstRun=true). Showing Settings dialog.");
    MaybeOfferFirstRunSettings();
}

// Erst DANACH der Tessdata-Dialog (Spec 0012), damit User im First-Run-Flow
// zuerst die Settings prüfen kann, bevor tessdata geladen wird.
MaybeOfferTessdataDownload();
```

`MaybeOfferFirstRunSettings()` ist analog zu `MaybeOfferTessdataDownload()`:
`SettingsDialog` instantiieren + `ShowDialog()` + Log + ggf. `_config = newConfig`
via `ApplyConfig()`.

## Reihenfolge beim ersten Start

1. Config laden → `loadedFromUserFile == false`, `_config.App.FirstRun == true`
2. **First-Run-Settings-Dialog** (neu) — User prüft + speichert/überspringt
3. Tessdata-Dialog (Spec 0012), nur wenn `Ocr.Engine == "tesseract"` + tessdata fehlt
4. TrayApp läuft normal weiter

## Tests

- **`UserConfigLocatorTests.cs`** NEU:
  - `LoadOrDefault_NoFile_ReturnsDefaults_LoadedFromUserFileFalse`
  - `LoadOrDefault_EmptyPath_ReturnsDefaults_LoadedFromUserFileFalse`
  - `LoadOrDefault_ValidFile_ReturnsConfig_LoadedFromUserFileTrue`
  - `LoadOrDefault_MalformedFile_ReturnsDefaults_LoadedFromUserFileFalse` (treat malformed as first-run)
- **`AppConfigTests.cs`** (oder bestehende Defaults-Tests):
  - `App_FirstRun_DefaultsToTrue`
- TrayApp-UI-Logik (`MaybeOfferFirstRunSettings`) bleibt manuell verifiziert
  (keine UI-Tests, analog Spec 0009/0012).

## Verworfen

- **Command-Line-Flag** `--first-run` zum Erzwingen: out-of-scope v0.1, könnte
  via `SettingsDialog.ResetToDefaults` + Re-Show simuliert werden.
- **Auto-Save der Defaults** ohne User-Interaktion: gegen das Prinzip „User
  muss Initial-Setup selbst bestätigen" (siehe Spec 0002).
- **Inline-Wizard** statt Settings-Dialog: doppelte UI-Pflege. Der bestehende
  SettingsDialog hat alle Felder; ein Wrapper mit „Überspringen"-Button ist
  ausreichend.
- **Tracking via FirstRun-Flag in User-Config** statt File-Existenz-Check:
  wäre redundant — wenn `config.json` existiert, war der User schon mal da.

## Out of Scope (v0.1)

- Welcome-Tour / Erklärungen zu jedem Setting (Tooltip-Texte sind im
  PropertyGrid vorhanden, keine zusätzliche Tour).
- „Don't show again"-Setting außerhalb von `App.FirstRun`: redundant.
- Mehrere First-Run-Profile (z. B. „Light User" vs „Power User"): YAGNI,
  der User kann die Settings manuell anpassen.

## Offene Punkte (alle abgeschlossen 2026-07-15)

- [x] **Martin**: Soll der Dialog `Owner = null` haben (analog Tessdata) oder
      `Owner = Hauptfenster`? Mein Vorschlag: **null**, weil beim ersten
      Start das Hauptfenster noch nicht da ist und ein Owner-Setting eine
      NullRef wirft, wenn die Reihenfolge kippt. — **erledigt**, Dialog
      wird in `TrayAppContext.MaybeOfferFirstRunSettings` mit `Owner = null`
      instanziert (analog zum Tessdata-Dialog).
- [x] **Martin**: Soll „Überspringen" einen Hinweis-Ballon zeigen („Du kannst
      jederzeit über das Tray-Menu die Settings öffnen")? Mein Vorschlag:
      **JA**, weil der User beim bloßen Cancel ggf. nicht versteht, dass er
      später rankommt. — **erledigt**, `TrayIconController.ShowBalloon`
      mit `ToolTipIcon.Info` und 5 s Timeout im Skip-Pfad.
- [x] **Martin**: Bestätigen, dass `App.FirstRun` als Settings-Property im
      PropertyGrid erscheinen soll (User kann es nachträglich togglen).
      Mein Vorschlag: **JA**, sonst ist das Verhalten nicht transparent.
      — **erledigt**, `AppSettings.FirstRun` hat ein `[Description(...)]`-
      Attribut und wird vom dynamischen `SettingsDialog`-PropertyGrid
      automatisch gerendert.
- [x] Folgefrage: Was, wenn `App.FirstRun == true` aber die Datei **existiert**
      und `App.FirstRun == true` (z. B. User hat es manuell in die Config
      editiert)? Mein Vorschlag: **dann Dialog nicht zeigen** — Bedingung ist
      „kein User-File", nicht „FirstRun-Flag == true". — **erledigt**,
      `MaybeOfferFirstRunSettings` returnt frühzeitig, wenn
      `loadedFromUserFile == true`, unabhängig vom Flag.