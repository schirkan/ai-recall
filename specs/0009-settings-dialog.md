# 0009 — Settings-Dialog (JSON Config Editor)

> **Status:** ✅ **v1.0 ABGESCHLOSSEN (2026-07-04 22:30)** — Implementiert in Spec 0006 Schritt 6
> **Owner:** Martin
> **Abhängig von:** Spec 0006 (Tray-EXE Foundation), Spec 0002 (MVP1-Config-Schema)

## Ziel

Eigenes Fenster zum Bearbeiten der JSON-Config (`%APPDATA%/AiRecall/config.json`),
aufrufbar über Tray-ContextMenu. Validiert Eingaben, schreibt atomar zurück,
triggert Hot-Reload des in-process `TriggerSupervisor`.

## UI

- Hauptfenster (`SettingsDialog`), modal zur TrayApp
- **Links**: `TreeView` der Config-Sektionen (Root + Sub-Sektionen):
  ```
  Config
  ├─ Logging
  ├─ Ocr
  ├─ Conversion
  ├─ Trigger
  └─ AppReader
     ├─ Browser
     ├─ Documents
     ├─ Explorer
     ├─ Notepad
     └─ Pdf
  ```
- **Rechts**: dynamisch generiertes Editor-Panel pro selektierter Sektion (Labels + Type-spezifische Editoren aus POCO-Reflection + Description-Label pro Property); Layout passt sich beim Splitter-Drag und Fenster-Resize an (`RelayoutEditors`).
- **Unten**: `ToolStrip` mit `Save`, `Cancel`, `Reload from Default`, `Reset Section`
- **Status-Bar**: zeigt Validierungsfehler pro Property, Pfad zur Config-Datei, Modified-Flag
- **Fenster-Position**: alle Dialoge erscheinen einheitlich **unten rechts** am Bildschirmrand mit 20 px Padding (`WindowPlacement.PositionBottomRight`, Bug-Bash 2026-07-06 I-UE). Vorteile: konsistente Position, verdeckt nicht das aktive Foreground-Fenster, User findet das Fenster schnell wieder.

## Config-Schema via Reflection

**Single Source of Truth**: `AiRecall.Core.Configuration.AppConfig` + alle Sub-POCOs.
- Property-Descriptoren via `TypeDescriptor.GetProperties(typeof(T))`
- Custom-Attributes:
  - `[Description("Maximum text size in KB...")]` → Label unter dem Editor (1-zeilig, kleiner grauer Text, Bug-Bash 2026-07-06 I-25)
  - `[DefaultValue(64)]` → Reset-Marker
  - `Required` (via Custom `IConfigurationValidator`-Interface) → Validation
  - **Property-Labels**: Property-Name wird camelCase → Title Case konvertiert
    (`maxTextLengthKB` → `Max Text Length KB`). Kein separates `[DisplayName]`
    nötig — der POCO-Property-Name IST der Label.
- **Custom-Type-Editoren** (aus `PropertyEditorFactory`):
  - `bool` → CheckBox
  - `int`/`long` mit Range → NumericUpDown
  - `string` → TextBox
  - `Enum` → ComboBox-Dropdown
  - `List<string>` → Comma-Separated Editor (TextBox mit Validation)
  - POCO-Sub-Configs (z. B. `CdpConfig`, `MarkdownSettings`, `WinEventSubscription`,
    `TriggerBlacklist`, `OneNoteConfig`, `TeamsConfig`) werden **rekursiv zu eigenen
    Sub-Sektionen** im TreeView (Bug-Bash 2026-07-06 I-18, siehe unten).

### ConfigSchemaReflection

```csharp
public static class ConfigSchemaReflection
{
    public static IReadOnlyList<ConfigSectionDescriptor> GetTopLevelSections(AppConfig config);
    public static ConfigSectionDescriptor? FindSection(AppConfig config, string path);
    public static PropertyDescriptor? GetProperty(AppConfig config, string path);
}

// Rekursiv: ein Section-Descriptor enthaelt SubSections fuer alle POCO-Properties
// vom Typ einer "expandierbaren" Config-Klasse.
public sealed class ConfigSectionDescriptor
{
    public string Name { get; }                  // z.B. "browser"
    public string DisplayName { get; }           // z.B. "Browser" (Title Case)
    public string Path { get; }                  // z.B. "appReader.browser"
    public Type SectionType { get; }             // typeof(BrowserConfig)
    public object Instance { get; }              // die POCO-Instanz aus AppConfig
    public IReadOnlyList<ConfigSectionDescriptor> SubSections { get; }
    public IReadOnlyList<PropertyDescriptor> Properties { get; } // Top-Level-Properties (nicht SubConfigs)
}
```

#### Rekursive Section-Auflösung (Bug-Bash 2026-07-06 I-18)

`BuildSection(instance, name, displayName, sectionType, parentPath)` läuft
rekursiv: für jede Property des POCOs wird geprüft, ob der Typ
„expandierbar" ist (`IsExpandableConfigType`). Expandierbar sind POCOs
mit eigenem `[Description]`-Attribut oder Properties, oder POCOs die
bereits in der Liste der Top-Level-Sections auftauchen. Wenn ja:
- Property wird **nicht** als PropertyDescriptor in `Properties` aufgenommen
  (sonst wäre sie doppelt editierbar)
- Stattdessen wird rekursiv ein neuer `ConfigSectionDescriptor` erzeugt
  und in `SubSections` eingereiht.

`IsExpandableConfigType` akzeptiert:
- POCOs mit ≥ 1 Property (z. B. `CdpConfig`, `MarkdownSettings`)
- Whitelist: bekannte Sub-Configs aus dem AppConfig-Tree
  (`TriggerBlacklist`, `WinEventSubscription`, `OneNoteConfig`,
  `TeamsConfig`, …)

Vorher (v1.0): Sub-Sub-Konfigs waren im TreeView unsichtbar (weder
Property noch SubSection — harte Top-Level-Liste). Jetzt: echte
Baumstruktur.

Beispiel-Tree nach I-18:
```
Config
├─ Capture
├─ Screen Recorder
│   └─ (leer — keine Sub-Configs)
├─ OCR
├─ Logging
├─ App Reader
│   ├─ Browser
│   │   ├─ CDP
│   │   └─ Markdown
│   ├─ Documents
│   ├─ Outlook
│   │   └─ Html To Markdown
│   ├─ Explorer
│   ├─ Notepad
│   ├─ Pdf
│   ├─ OneNote
│   └─ Teams
├─ Trigger
│   ├─ Win Events
│   └─ Blacklist
│       ├─ Window Classes
│       ├─ Processes
│       └─ Window Titles
└─ Conversion
```

## Validation

- **Pre-Save-Validation**: alle Properties durchlaufen, Fehler sammeln
- Pro Property:
  - `Required`-Check (Display-Name + Required-Custom-Attribute)
  - Range-Validation (Min/Max aus Attributen)
  - Enum-Validation (nur gültige Werte)
  - Path-Validation (für Pflicht-Pfade, z. B. `tessDataPath`)
- Fehler werden pro Property angezeigt: roter Rahmen + Tooltip + Status-Bar
- Save-Button wird disabled bei Validierungsfehlern

## Persistenz

- **Load**:
  1. `%APPDATA%/AiRecall/config.json` (falls existiert) → Deserialize via `ConfigLoader`
  2. Sonst: `default-config.json` (im Output-Verzeichnis der TrayApp) → als Template
  3. Bei Parse-Error: Backup der kaputten Datei als `config.json.broken-{timestamp}` und Default laden
- **Save**:
  1. Validation durchlaufen
  2. Backup der existierenden Datei als `config.json.bak`
  3. Serialize `AppConfig` zu JSON (mit `JsonNamingPolicy.CamelCase`)
  4. Atomic write: `config.json.tmp` schreiben → `File.Move(tmp, config.json, overwrite: true)`
  5. Modified-Flag auf SettingsDialog zurücksetzen
- **Hot-Reload (in-process, revidiert v0.2)**:
  - Nach Save → `TriggerSupervisor.RestartAsync(newConfig)` auf UI-Thread
  - Bei Restart-Fehler → Rollback auf Backup + Warning-Dialog
  - TriggerSupervisor disposet den alten `ITriggerService` sauber und startet neuen mit neuer Config
  - **Keine Prozess-Kill**, **kein Cold-Start**, **kein MMF-Reinit**

## Tests

- `SettingsDialogTests`:
  - Round-Trip: Load → Modify → Save → Reload → assert equal (alle Felder)
  - Validation: invalid Enum → Save-Button disabled, Fehler-Tooltip sichtbar
  - Default-Fallback: User-Config existiert nicht → Default wird geladen, kein Crash
  - Atomic-Write: simulierter Crash mid-write → Original-File intakt, .tmp entfernt
  - Backup-Erstellung: bei Save wird `.bak` korrekt angelegt
  - **Property-Layout** (Bug-Bash I-25): Description-Label wird unter dem Editor platziert, Splitter-Drag resized Editoren sauber, kein Überlappen
  - **Window-Position** (Bug-Bash I-UE): Dialog erscheint BottomRight (20 px Padding) auf `PrimaryScreen.WorkingArea`
- `ConfigSchemaReflectionTests`:
  - GetTopLevelSections gibt alle 7 Top-Level-Sektionen zurück
    (Capture, ScreenRecorder, OCR, Logging, AppReader, Trigger, Conversion)
  - **Rekursiv** (Bug-Bash I-18): Sub-Sections werden korrekt aufgelöst
    (z. B. `AppReader → Browser → CDP → enabled`)
  - GetProperty mit Pfad `"appReader.browser.cdp.enabled"` liefert korrektes PropertyDescriptor
  - FindSection mit Pfad `"trigger.blacklist.windowClasses"` liefert korrekte Section
  - IsExpandableConfigType klassifiziert POCOs korrekt (Whitelist + Heuristik)
- `HotReloadTests` (revidiert v0.2):
  - SettingsDialog Save → TriggerSupervisor.RestartAsync wird aufgerufen
  - Mock-TriggerSupervisor prüft: alter Service disposed, neuer Service mit neuer Config gestartet

## Update 2026-07-06 (Bug-Bash Teil 2)

Bug-Bash 2026-07-06 (Commit `d245dd2`) hat Spec 0009 in vier Bereichen
verbessert: **rekursiver TreeView, Description-Labels, einheitliche
Fenster-Positionierung, proportional Splitter.**

| # | Thema | Entscheidung | Begründung |
|---|---|---|---|
| 1 | Rekursiver TreeView | `BuildSection` läuft rekursiv über POCO-Properties; `IsExpandableConfigType` filtert expandierbare POCOs | Vorher: 15+ flache Top-Level-Knoten, Sub-Sub-Konfigs (`browser.cdp`, `trigger.winEvents`, `trigger.blacklist.windowClasses`) waren im Tree unsichtbar. User fand z. B. CDP-Einstellung nicht über die UI, nur über direkte JSON-Edit. Nachher: echte Baumstruktur mit allen Sub-Sections, TreeView expandierbar. |
| 2 | Description-Attribute statt DisplayName | POCO-Properties tragen `[Description("...")]` (aus `System.ComponentModel`); SettingsDialog zeigt Description als 1-zeiliges Label **unter** dem Editor | Bug-Bash I-19/I-25: 77 `[Description]`-Attribute wurden in einem Cluster auf `AppConfig` + alle Sub-POCOs verteilt. `[DisplayName]` wurde **entfernt** — der Property-Name IST der Label (camelCase → Title Case, z. B. `maxTextLengthKB` → `Max Text Length KB`). Spart Doppel-Pflege: POCO-Field hat einen Namen, ein Description. |
| 3 | WindowPlacement-Helper | Neue `WindowPlacement.PositionBottomRight(form, padding=20)` in `AiRecall.TrayApp.Windows` | Bug-Bash I-UE: vor Bug-Bash waren `SettingsDialog` und `LogviewerWindow` zentriert (WinForms-Default) — Dialoge verdeckten das aktive Foreground-Fenster. Nach Bug-Bash: alle Dialoge erscheinen BottomRight mit 20 px Padding auf `Screen.PrimaryScreen.WorkingArea`. Bei zu großem Form wird auf Working-Area herunterskaliert. |
| 4 | Proportional Splitter | `SplitterDistance` als relativer Wert (z. B. 200 px oder 25 % der Breite), `SplitterWidth = 6`, `SplitterMoved`-Event resized Editor-Panel | Bug-Bash I-16: vor Bug-Bash war `SplitterDistance` als fixer Pixelwert (z. B. 250) gesetzt — bei kleinen Fenstern war TreeView zu breit, Editor-Panel zu schmal. Nach Bug-Bash: proportional zur Form-Breite, `RelayoutEditors()` wird auf `SplitterMoved` und Form-Resize aufgerufen, Description-Label unter Editor passt sich an. |
| 5 | Tests | 56 neue Tests in `PropertyEditorFactoryTests` + erweiterte `ConfigSchemaReflectionTests` | Recursive-Section-Auflösung testbar (Test-Baum mit Mock-POCOs), `WindowPlacement` indirekt testbar (Form mit Mock-`Screen` ist schwierig — wird manuell verifiziert). |
| 6 | Test-Count | 443 → **673/673 grün** | +86 PeriodicCaptureThreadTests + 187 TessdataManagerTests (Spec 0012-Vorbereitung) + 56 PropertyEditorFactoryTests + weitere aus Bug-Bash Teil 2 |

### Layout-Beispiel (nach Bug-Bash Teil 2)

```
┌─ AiRecall - Settings ─────────────────────────────────────────┐
│                                                               │
│ ┌── Config ──┐ ┌──── Browser > CDP ─────────────────────────┐ │
│ │ App Reader │ │ Cdp Enabled      [x]                        │ │
│ │ ├─ Browser │ │ CDP Endpoint     [http://localhost:9222    ]│ │
│ │ │  ├─ CDP  │ │   CDP-Endpoint, typisch http://localh...   │ │
│ │ │  └─ Mark │ │ Cdp Timeout Ms   [1500       ] ▲▼           │ │
│ │ ├─ Outlook │ │   Timeout (ms) fuer HTTP-Lookup + WebS...   │ │
│ │ ├─ Teams   │ │                                             │ │
│ │ └─ ...     │ │                                             │ │
│ │ Trigger    │ └─────────────────────────────────────────────┘ │
│ │ Logging    │ [ Save ]  [ Cancel ]  [ Reload ]  [ Reset ]   │
│ └────────────┘ [Plausible: bottom-right, 20 px padding]      │
└───────────────────────────────────────────────────────────────┘
        ↑ TreeView rekursiv        ↑ Description unter jedem Editor
```

### Verworfen (Bug-Bash Teil 2)

- **PropertyGrid-Control statt dynamischer Editoren**: WinForms 8 hat
  kein `PropertyGrid` (entfernt). Dynamische Form-Generierung ist die
  einzige Option in .NET 8 windows.
- **Description als Tooltip am Editor**: Tooltips verschwinden nach 5 s,
  sind nicht dauerhaft sichtbar. Label unter dem Editor ist
  barrierefreier und reviewbar.
- **Manuelle Pflege einer Section-Liste**: wäre Drift-Quelle (jede neue
  Sub-Config müsste in 2 Stellen gepflegt werden). Reflection ist
  Single-Source-of-Truth.
- **`WindowPlacement` mit DPI-Skalierung**: Working-Area ist DPI-aware,
  kein zusätzlicher Code nötig.

## Verworfen

- **Settings via .NET User-Settings-Properties** (`Properties.Settings.Default`): zu limitiert, kein JSON-Roundtrip, driftet von `default-config.json`.
- **Settings in DB (SQLite)**: Overkill, JSON ist Martin-Default + einfach editierbar.
- **Settings-Dialog mit eigenem Schema-File (manuell gepflegt)**: Drift-Risiko, Reflection auf POCOs ist Single-Source-of-Truth.
- **WebView-basierter Editor (HTML/CSS)**: Overkill, WinForms-PropertyGrid reicht für strukturierte Config.
- **Auto-Save on Change**: zu risky, User soll explizit Save klicken.
- **Schema-Validation via JSON-Schema-Draft-07**: zusätzliche Drift-Quelle (Schema-File + POCO), POCO-Attributes reichen.
- **Hot-Reload via Process-Kill+Restart** (Spec 0006 v0.1): durch in-process-Architektur (revidiert v0.2) unnötig.

## Offene Punkte

- **PropertyGrid-Default-Editor für komplexe Typen** (z. B. `AppReaderConfig` mit Sub-Properties): rekursiv oder flach? → **Entscheidung**: flach mit Sektion-Tree (jede Sub-Sektion eigener PropertyGrid-Tab), einfacher mental model.
- **Passwort-Felder für sensible Configs**: aktuell keine sensiblen Felder in `AppConfig`. Falls später (z. B. Browser-CDP-Auth): `PasswordPropertyText`-Attribute nutzen.
- **Diff-Ansicht "was hat sich geändert" vor Save**: nice-to-have, optional via Diff-Library.
- **Reset-to-Defaults-Button pro Sektion**: planned (siehe UI §ToolStrip).
- **Settings-Import/Export** (komplettes JSON-File tauschen): über `Save As...` machbar, YAGNI für MVP2.
- **Multi-Language-Labels**: erstmal DE + EN hardcoded, später i18n.