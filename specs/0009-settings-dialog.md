# 0009 — Settings-Dialog (JSON Config Editor)

> **Status:** 📝 Draft v0.1 (2026-07-04) — Martin-Review ausstehend
> **Owner:** Martin
> **Abhängig von:** Spec 0006 (Tray-EXE Foundation), Spec 0002 (MVP1-Config-Schema)

## Ziel

Eigenes Fenster zum Bearbeiten der JSON-Config (`%APPDATA%/AiRecall/config.json`),
aufrufbar über Tray-ContextMenu. Validiert Eingaben, schreibt atomar zurück,
triggert Hot-Reload des Subprozesses.

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
- **Rechts**: `PropertyGrid` pro selektierter Sektion mit Type-Editoren aus POCO-Reflection
- **Unten**: `ToolStrip` mit `Save`, `Cancel`, `Reload from Default`, `Reset Section`
- **Status-Bar**: zeigt Validierungsfehler pro Property, Pfad zur Config-Datei, Modified-Flag

## Config-Schema via Reflection

**Single Source of Truth**: `AiRecall.Core.Configuration.AppConfig` + alle Sub-POCOs.
- Property-Descriptoren via `TypeDescriptor.GetProperties(typeof(T))`
- Custom-Attributes:
  - `[DisplayName("Max Text KB")]` → PropertyGrid-Label
  - `[Description("Maximum text size in KB...")]` → Tooltip
  - `[Category("Limits")]` → Grouping
  - `[DefaultValue(64)]` → Reset-Marker
  - `Required` (via Custom `IConfigurationValidator`-Interface) → Validation
- **Custom-Type-Editoren**:
  - `List<string>` → Comma-Separated Editor (TextBox mit Validation)
  - `Enum` → Dropdown-Editor (Standard)
  - `int`/`long` mit Range → NumericUpDown (Standard)
  - `bool` → Checkbox (Standard)

### ConfigSchemaReflection

```csharp
public static class ConfigSchemaReflection
{
    public static IEnumerable<ConfigSectionDescriptor> GetSections(AppConfig config);
    public static PropertyDescriptor GetProperty(AppConfig config, string path);
}

public sealed class ConfigSectionDescriptor
{
    public string Name { get; }
    public string DisplayName { get; }
    public Type SectionType { get; }
    public object Instance { get; }       // POCO-Instanz aus AppConfig
    public PropertyDescriptorCollection Properties { get; }
}
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
- **Hot-Reload**:
  - Nach Save → TrayApp's `ProcessSupervisor.Stop()` → `Start()` mit neuer Config
  - Bei Stop-Fehler → Rollback auf Backup + Warning-Dialog

## Tests

- `SettingsDialogTests`:
  - Round-Trip: Load → Modify → Save → Reload → assert equal (alle Felder)
  - Validation: invalid Enum → Save-Button disabled, Fehler-Tooltip sichtbar
  - Default-Fallback: User-Config existiert nicht → Default wird geladen, kein Crash
  - Atomic-Write: simulierter Crash mid-write → Original-File intakt, .tmp entfernt
  - Backup-Erstellung: bei Save wird `.bak` korrekt angelegt
  - PropertyGrid-Reflection: alle POCO-Properties mit Display-Attributen korrekt angezeigt
- `ConfigSchemaReflectionTests`:
  - GetSections gibt alle Top-Level-Sektionen zurück (Logging, Ocr, Conversion, Trigger, AppReader)
  - GetProperty mit Pfad `"appReader.browser.cdp.enabled"` liefert korrektes PropertyDescriptor
  - Required-Attribute wird korrekt erkannt

## Verworfen

- **Settings via .NET User-Settings-Properties** (`Properties.Settings.Default`): zu limitiert, kein JSON-Roundtrip, driftet von `default-config.json`.
- **Settings in DB (SQLite)**: Overkill, JSON ist Martin-Default + einfach editierbar.
- **Settings-Dialog mit eigenem Schema-File (manuell gepflegt)**: Drift-Risiko, Reflection auf POCOs ist Single-Source-of-Truth.
- **WebView-basierter Editor (HTML/CSS)**: Overkill, WinForms-PropertyGrid reicht für strukturierte Config.
- **Auto-Save on Change**: zu risky, User soll explizit Save klicken.
- **Schema-Validation via JSON-Schema-Draft-07**: zusätzliche Drift-Quelle (Schema-File + POCO), POCO-Attributes reichen.
- **Settings-Dialog ohne Hot-Reload** (User muss TrayApp neustarten): UX-regression.

## Offene Punkte

- **PropertyGrid-Default-Editor für komplexe Typen** (z. B. `AppReaderConfig` mit Sub-Properties): rekursiv oder flach? → **Entscheidung**: flach mit Sektion-Tree (jede Sub-Sektion eigener PropertyGrid-Tab), einfacher mental model.
- **Passwort-Felder für sensible Configs**: aktuell keine sensiblen Felder in `AppConfig`. Falls später (z. B. Browser-CDP-Auth): `PasswordPropertyText`-Attribute nutzen.
- **Diff-Ansicht "was hat sich geändert" vor Save**: nice-to-have, optional via Diff-Library.
- **Reset-to-Defaults-Button pro Sektion**: planned (siehe UI §ToolStrip).
- **Settings-Import/Export** (komplettes JSON-File tauschen): über `Save As...` machbar, YAGNI für MVP2.
- **Multi-Language-Labels**: erstmal DE + EN hardcoded, später i18n.