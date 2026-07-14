# DECISIONS

Architektur- und Stack-Entscheidungen mit Datum und Begründung. Wird bei
Bedarf von PROJECT.md oder specs/*.md geladen.

---

## 2026-07-14 — Spec 0014 (Tray Audio Indicator + Manual Audio Control) v1.0 ABGESCHLOSSEN

**Anlass:** Spec 0014 v1.0 abgeschlossen nach Iter. 1+1b+2+3 + Flake-Fix + Doc-Cluster.
Commits: `a8a70e3` (Iter. 1) → `07575bc` (Iter. 1b) → `1a715a3` (Iter. 2) →
`1d6ef22` (Iter. 3) → `2814d5b` (Flake-Fix) → Doc-Cluster (dieser Commit).
Test-Stand: 820/820 (nach Iter. 3) → 829/829 (nach `058c023` App-Capture-Helper) → 829/829 stabil nach Flake-Fix (5/5 Counter/Async-Runs).

| # | Thema | Entscheidung | Begründung |
|---|---|---|---|
| 1 | Tray-Icon-Priorität (Iter. 2) | **Audio > Capture > Idle** (3-stufige Kaskade). Audio-Recording zeigt roten Kreis + „M" (`tray-audio-recording.ico`), Capture zeigt 👁️ (`tray-recording.ico`), Idle zeigt ⚫ (`tray-idle.ico`). | Martin-Direktive: „Bei laufender Audio-Aufzeichnung soll sich das Icon ändern". Audio hat Vorrang vor Capture, weil Audio explizit vom User ausgelöst wurde — Capture ist Hintergrund. Invariante explizit testbar als `internal static string ResolveTrayIconKey(TriggerState, bool)`. |
| 2 | Gate-First-Pattern (Iter. 3) | **Privacy-First-Gate VOR State-Logik** in `ApplyRecordingEnabledState()`. Reihenfolge: (1) Audio.Enabled-Check, (2) IsRecording-Check, (3) Enable/Disable-Logik. Niemals State-Logik vor Gate-Check, sonst kann State den Gate umgehen. | Bug während Iter. 3: `IsRecording=true` enablete Stop-Item obwohl `Audio.Enabled=false`. Fix: beide Items disabled wenn Gate nicht erfüllt, unabhängig von `IsRecording`. Lesson: bei „enabled iff Gate AND State" → Gate ZUERST. |
| 3 | Single-Active-Recording-Constraint (Iter. 1b) | **`StartManualAsync()` wirft `InvalidOperationException`** wenn schon eine Aufnahme läuft (egal ob Auto oder Manual). User muss erst `StopAsync()` aufrufen. | Martin-Direktive 2026-07-10 19:11. Variante A aus 3 Vorschlägen (InvalidOperationException, Auto-Stop+Wins, Silent-NoOp). Klar + deterministisch; Tray-Menu-Items sorgen für korrekten Enabled/Disabled-State, sodass User gar nicht erst klicken kann wenn schon läuft. |
| 4 | IRecordingControl-Provider-Pattern (Iter. 3) | **`Func<IRecordingControl?>? recordingControlProvider = null`** als optionaler Konstruktor-Parameter im `TrayIconController`. `RebindRecordingControl()` idempotent via `ReferenceEquals` — alte Control via `-=` abmelden, neue via `+=` anmelden. Wird bei jedem `Supervisor.StateChanged` aufgerufen, damit Hot-Reload (Service-Restart mit neuer `MeetingTrigger`-Instanz) automatisch die richtige Control bindet. | Pattern analog zum `ConversionWorker`-Pattern in `TriggerService` (Spec 0007). Vermeidet harte Service-Abhängigkeit im Tray-Controller → testbar mit `FakeRecordingControl` ohne WinForms-Threading. |
| 5 | ToolStripMenuItem.Visible .NET-8 Quirk | **`Visible = false` ist Default** in .NET 8 / WinForms (anders als ältere .NET Framework). Tests prüfen NUR `Enabled`, NICHT `Visible` — sonst flaken Tests je nach Framework-Version. | Empirisch beobachtet beim Schreiben der `TrayIconControllerAudioItemsTests`. Pragmatisch: Gate über `Visible` (User sieht Items erst wenn Audio.Enabled=true), State über `Enabled` (klickbar/nicht klickbar). |
| 6 | async-void-Race-Fix (`2814d5b`) | **`OnPresenceChanged` ist nicht mehr `async void`**, sondern synchron + Fire-and-Forget via `_ = StopRecordingFireAndForgetAsync(...)` mit eigenem try/catch (kein unbeobachteter Task). | Sporadischer Flake in `MeetingTriggerTests.RecordingStateChanged_Fired_OnAutoStop` (1-2 von 5 Runs). Root-Cause: `async void` Event-Handler yieldet am ersten await; Continuation läuft mit `ConfigureAwait(false)` auf Threadpool, Test-Thread liest parallel → `List<T>.Add` race (List<T> ist nicht thread-safe). Fix 5/5 Counter/Async-Runs grün. Lesson: `async void` nur für top-level Event-Handler wenn die Aufruf-Semantik explizit fire-and-forget ist UND keine Test-Synchronisation nötig. In Tests wird das meistens verletzt. |

### Martin-Direktiven 2026-07-10 (Übersicht)

| # | Thema | Direktive | Auswirkung |
| - | - | - | - |
| Q | Tray-Icon-Indikator | „Bei laufender Audio-Aufzeichnung soll sich das Icon ändern" | Audio > Capture > Idle Priorität (Decision 1) |
| R | Manuelle Steuerung | „Steuerung für Audio-Aufzeichnung im Tray-Menu (Start, Stop)" | Iter. 3 mit `Func<IRecordingControl?>?`-Provider-Pattern (Decision 4) |
| S | Single-Active-Constraint | „Es kann immer nur eine aktive Audio-Aufnahme geben" | `StartManualAsync` wirft `InvalidOperationException` (Decision 3) |
| T | API-Vereinfachung | „`StopAsync()` ohne Key-Parameter" | `StopAsync(string?)` → `StopAsync()` in Iter. 1b |

### Lessons

- **Iter.-Plan mit Sub-Commits pro logischer Einheit** (Iter. 1, 1b, 2, 3, 4) statt eines Mega-Commits → gezieltes Review/Revert pro Schicht möglich.
- **Counter/Async-Regel** (10+ Runs für Flake-Detektion, DECISIONS.md 2026-07-09): vor diesem Fix 1/3 Runs flake, nach Fix 5/5 stabil. Lesson: async-void-Pattern in produktivem Code mit Tests = fast immer Race-Kandidat, auch wenn Test-Suite in Isolation läuft.
- **Pattern-Wiederverwendung**: `Func<IRecordingControl?>?`-Provider-Pattern + `_owns*`-Flag ist eine Variante des `ConversionWorker`-Patterns (Spec 0007) und des `MeetingTrigger`-Patterns (Spec 0013 Iter. 4). Martin-approved Standard für optionale, hot-reloadbare Komponenten.

### Folge-Cluster (nach v1.0-Abnahme)

- **Spec 0014 Iter. 3.1**: `trigger_source: manual-audio` im MD-Frontmatter (Parametrisierung von `RecordingSession.WriteInitialMetaMd`), bisher hardcoded `polling`.
- **Outlook-Speaker-Mapping** (Spec 0013 v0.4): wartet auf v0.3-Abnahme (bereits abgenommen am 2026-07-14).

---

## 2026-07-09 — MVP 3 Audio Notes IMPLEMENTATION (Spec 0013 v0.3 abgeschlossen)

**Anlass:** Iter. 1-4 Implementations-Cluster nach der Spec-Definition
vom 2026-07-07. Komplette Pipeline von Teams-Meeting-Polling bis
MD-Aktualisierung mit Diarization in 11 Commits gepusht. Commits:
`88cf4f7` (Iter. 1) → `787c151` (Iter. 2) → `8d77e7a`/`725f352`/`c278616`/
`b21411a`/`c292b25`/`56965c6`/`2d79f7f`/`ff97767` (Iter. 3a-g) → `92480e7`
(Iter. 4). Stand 22:50: 7 Commits lokal + 4 Commits, alle gepusht, Working
Tree clean, **777/777 Tests grün stabil in 5/5 Runs**.

**Sub-Iterations-Architektur (Martin-Direktive, Sub-Commits pro
Komponente):** Iter. 3 wurde in 7 Sub-Commits (3a-3g) zerlegt statt einem
großen — saubere Trennung pro Komponente (Stereo-Concatenator, Provider-
Interface, je 1 Provider, Worker-Init, Worker-Refactor, Trigger-Wiring,
Connection-Tester). Gezieltes Review/Revert pro Provider möglich.

**Pattern: ConversionWorker-Pattern wiederverwendet.** Der
`TranscriptionWorker` (Spec 0013) folgt 1:1 dem `ConversionWorker`-
Pattern (Spec 0007): ctor startet Background-Pool auto, Channel<T> +
SemaphoreSlim(maxN), Counter via Interlocked (`PendingCount/
CompletedCount/FailedCount`), `IDisposable` für Service-Lifecycle,
optional ctor-Param + `_owns*` Flag, Recovery-Scan. **Martin-approved
Standard für Background-Worker im Projekt** — bei neuen Workern
(ConfigurationWorker, IndexWorker, etc.) diesem Pattern folgen.

**TriggerSupervisor-Integration (Iter. 4, `92480e7`).**
`MeetingTrigger` wird von `TriggerService` (nicht von `TriggerSupervisor`
direkt) initialisiert/beendet — analog zur bestehenden
`ConversionWorker`-Composition. Neue Factory
`MeetingTriggerFactory.TryCreateDefault(config, logger)` baut die
Production-Default-Composition mit Privacy-First-Gate:

```text
if (!Audio.Enabled || !Teams.AutoRecordMeetings || !AppReader.Teams.Enabled) return null;
// sonst: Poller + Provider-Default + TranscriptionWorker + RecorderFactory + Devices
```

`MeetingTrigger` wurde nachträglich auf `IDisposable + IAsyncDisposable`
erweitert; sync-`Dispose()` ruft `DisposeAsync().AsTask().GetAwaiter().
GetResult()` (saubere Sync-Bridge ohne ConfigureAwait-Verlust).

**Bug-Fix: TranscriptionWorker Counter-Race (in Iter. 4 enthalten).**
Beim Stabilitätstest (5 Runs) entdeckt: 2/5 Runs scheiterten mit 40 %
Flake-Rate. Ursache: `_failedCount`-Increment stand VOR dem
`await _metadata.MarkFailedAsync()`-Aufruf. Tests warten per
`WaitUntilAsync(() => worker.FailedCount == 1)` auf den Counter, prüfen
danach `File.Exists(meta)` — Race: Counter steigt bevor `meta.md`
geschrieben ist. Fix: Increment NACH `await MarkFailedAsync` verschoben.
Production-Code-Fix, kein Test-Hack. Lesson für künftige Worker:
**Counter incrementieren erst NACH Side-Effect**, sonst observability
ohne tatsächliche Vollständigkeit.

**Privacy-First ist nicht verhandelbar.** Drei unabhängige Gates
hindern `MeetingTrigger` an unbeauftragter Composition: `Audio.Enabled`,
`TeamsConfig.AutoRecordMeetings`, `AppReaderConfig.Teams.Enabled`. Alle
drei Default-true (Teams) bzw. Default-false (Audio, Privacy) — User
muss Auto-Recording bewusst aktivieren. In Tests verifiziert
(`MeetingTrigger_*_PropertyIsNull` + `MeetingTrigger_ExternallyInjected_*`).

**Lessons für PowerShell-Windows-Development (Martin-relevant):**

1. **Commit-Message via UTF-8-File** (umgeht cp1252-Bug): PowerShell 5.1
   übergibt Argumente in Windows-Console-Codepage an externe Prozesse.
   `git commit -m "äöü"` schreibt die cp1252-Bytes ins Commit-Objekt
   (kaputte Umlaute permanent). Workaround: Commit-Message in
   `temp/commit-X.txt` via `write`-Tool (UTF-8) + `git commit -F <file>`.
   Für `git push`: PowerShell-Single-Quotes-Pattern (`git -C 'C:\...' push
   origin main`) verhindert Pfad-/Argument-Expansion.

2. **PowerShell `~\` + `git -C` ist ein Antagonismus.** `cd '~\...'`
   expandiert `~\` zu HOME; `git -C '~\...'` versucht wörtlich den
   `~\`-Pfad und scheitert mit „No such file". Lösung: expliziter
   Backslash-Pfad mit Drive-Letter, oder `cd` + `git` ohne `-C`.

3. **`git diff --stat` zeigt LF→CRLF-Warnings als „error"**. OpenClaws
   `exec`-Layer stuft die Git-Warnings als stderr ein → `2>&1` Returncode
   bei eigentlich erfolgreichem Build. Bei Verdacht: Original-Output
   prüfen (z. B. `tail -10`), nicht nur Exit-Code.

4. **Test-Run-Stabilität**: 1 Run = unzuverlässig. Minimum 3 Runs vor
   Commit; bei Counter-/Async-Tests lieber 5. Heute: 776/777 (Flake,
   Counter-Race), 777/777, 777/777, 777/777, 777/777 → Bug aufgedeckt →
   Counter-Pattern-Fix → 5/5 Runs stabil grün.
   **Update 2026-07-10**: Auch 5 Runs sind nicht genug. Bei einem
   33%-Flake liegt P(0 Fehler in 5) = (0,67)^5 ≈ 13 %. Erst **10–12 Runs**
   detektieren mit >95 % Wahrscheinlichkeit. Beispiel: 12 Runs heute hatten
   4 Fehler (P(4 oder mehr) ≈ 8 % — wir hatten Pech im Bereich 8–13 %).
   **Faustregel**: 10+ Runs für Counter/Async-Pfade; jeder Run mit Verbose-Logger,
   damit das FAIL-Muster sichtbar wird.

**NAudio 2.2.1 API-Realität (für Iter. 1 hart erarbeitet):**

- `WaveFileReader.Read(float[], int, int)` existiert NICHT in 2.2.1.
  Pflicht: `waveFileReader.ToSampleProvider().Read(buffer, offset, count)`.
- `WaveFileWriter.WriteSamples(short[], int, int)` existiert NICHT.
  Workaround: `float[] = shorts.Select(s => s / 32768f).ToArray()` +
  `writer.WriteSamples(floats, …)`.
- `MMDevice.DeviceFriendlyName` = direkte Property, NICHT
  PropertyStore. PropertyStore-Indexer `this[string]` ist in NAudio 2.x
  entfernt → nur `Contains(PropertyKey)` + `this[PropertyKey]`.
- `WaveFileReader.SampleCount` = Frames (NICHT interleaved Samples).
  Stereo 16 kHz, 100 ms Aufnahme = 1600 Frames.
- General: NAudio 2.x ist breaking gegenüber 1.x (auch 2.0 → 2.2).
  Library-Wechsel: immer API-Diff prüfen.

**Poller-Test-Hook-Pattern (projektweit etabliert):** Klassen mit
Debounce (Poller, 30 s `MinMeetingDurationSeconds`) brauchen
Test-Hooks, weil reale Clock-Tests zu lange dauern würden. Lösung:
`internal void RaiseXxxChangedForTest(args)` per `InternalsVisibleTo`
für Test-Assembly. Production-Code nutzt weiter echte Detection-Pfade.

**Status:** MVP 3 v0.3 vollständig abgeschlossen. Nächste Stufe:
Spec 0013 v0.4 (Outlook-Speaker-Mapping) auf Martins „go" oder
MVP 4 Auto-Wiki.

---

## 2026-07-07 — MVP 3 Audio Notes (Spec 0013, v0.3 nach Martin-Feedback)

**Anlass:** Martins Anforderungsliste 2026-07-07 (Telegram-Topic „AI Recall",
8 Punkte):
1. Automatische Teams-Meeting-Start-Erkennung (Process + Window-Title)
2. Background-Audio-Recording mit zwei Kanälen
3. Mikrofon + Speaker-Loopback
4. Audio-Devices in Settings auswählbar
5. MD-Datei mit Metadaten neben Audio
6. Background-Worker transkribiert nach Ende (Provider noch offen)
7. Diarization als Pflicht
8. Transkription in MD-Datei (analog OCR-Pattern aus Spec 0007 / Bug-Bash I-17)

**Update 8 (2026-07-07, später):** Martin-Direktive _„Es muss auch automatisch
erkannt werden, wann ein Meeting endet, um die Aufnahme zu stoppen.
Generell sollte die Aufnahme in einem eigenen thread laufen, bis das stopp
Signal kommt. Dann werden die Audio Dateien geschrieben und die md datei
verlinkt beide. Der background worker liest diese dann später ein und startet
das transkript. Um das Meeting ende zu erkennen muss regelmäßig nach dem
Meeting Fenster gesucht werden."_ → Architektonische Erweiterung um
**Polling-basierte Anwesenheitserkennung** + **Recording-Lifecycle mit eigenem
Thread + Stop-Signal + MD-Stub-Pattern**. Neuer `MeetingPresencePoller` in
`src/AiRecall.Trigger/` ruft alle 5 s `TeamsAppReader.TryGetActiveMeetingAsync()`
auf und feuert `PresenceChanged` bei Edge-Detection (false→true = Started,
true→false = Ended). Polling ist **alleinige Quelle** für Start/Stop in v0.3;
Event-driven `MeetingStateChanged` aus Spec 0011/Update 2 bleibt im Code,
wird aber NICHT vom `TriggerSupervisor` abonniert. Selbst-heilend, robust
gegen verlorene Events (Teams-Reload, Network-Drop, UI-Crash). Aufnahme läuft
in eigenem Thread (`RecordingSession` via `Task.Run`), Stop via
`CancellationToken` (kein Thread.Abort). Beim Stop: Buffer-Snapshots →
`mic.wav` + `loopback.wav` schreiben → `meta.md` Status `recorded` mit
`audio_files`-Links setzen → Live-Enqueue im Worker. MD-Stub-Pattern:
`meta.md` zweistufig geschrieben (Start = `recording`, Stop = `recorded`),
neues Frontmatter-Feld `worker_task_enqueued` für Crash-Recovery-Scan des
`TranscriptionWorker`. Neue Komponenten: `MeetingPresencePoller`,
`RecordingSession`, `MeetingPresenceStateChangedEventArgs`,
`MeetingPresenceSnapshot`, `MetaMdLifecycleManager`, `MetaMdFrontmatter`,
`MeetingRecordingPaths`. Tests-Plan erweitert um ~18 neue Tests (~91 → ~109).
Martin-Direktiven-Status: 10 Direktiven (A–P) abgehakt.

**Update 7 (2026-07-07, später):** Martin-Direktive _„Azure speech auch
mit stereo nutzen."_ → Annahme „Azure Speech downmixt intern auf Mono"
aus Update 6 entfernt. Azure Speech verarbeitet das Stereo-File nativ
(kein Downmix auf unserer Seite), Response enthält `ChannelId` zusätzlich
zu `SpeakerId`. Beide Provider (Azure + Deepgram) bekommen das gleiche
`combined-stereo.wav` — keine Provider-spezifischen Pre-Processing-Pfade.
Output-MD nutzt kombinierte Channel-Speaker-Labels (z. B. „C0-S1"
für Channel 0 / Speaker 1) für Azure-Response.

**Update 6 (2026-07-07, später):** Martin-Direktive _„Nutze weiterhin
stereo mit beiden Kanälen."_ → §5.4 von Mono-Mix (Update 5) zurück auf
Stereo-Concatenation. `MonoMixer` ersetzt durch `StereoConcatenator`,
`combined-mono.wav` ersetzt durch `combined-stereo.wav`. Beide Kanäle
bleiben erhalten (Deepgram kann Multi-Channel-Diarization nutzen,
Azure Speech downmixt intern). RMS-Analyse bleibt gestrichen
(Update 5 unverändert gültig). Storage-Flexibilität für v0.4
(Per-Channel-Re-Transkription, Audio-Pre-Processing) ohne
Recording-Format-Change.

**Update 5 (2026-07-07, später):** Martin-Direktive _„Streiche das rms.
Diarization macht der Provider"_ → **Komplette Vereinfachung**: §5.5
Cross-Channel-Correlation (RMS) ersatzlos gestrichen. §5.4 von
Stereo-Concatenation auf Mono-Mix reduziert (`combined-mono.wav`
statt `combined-stereo.wav`). Datenmodell `TranscriptionResult`
schlanker (kein `LocalSpeakerId`, kein `RemoteSpeakerIds`, kein
`RoleMap` — nur `SpeakerLabels`). Output-MD zeigt rohe Provider-
Speaker-IDs (S0, S1, S2). Komponenten `SpeakerRoleAssigner`/
`SpeakerRoleMap`/`StereoConcatenator` entfernt, ersetzt durch
`MonoMixer`. Test-Plan von ~109 zurück auf ~91 Tests.
**§5.4-Mono-Mix-Teil in Update 6 zurückgenommen auf Stereo.**

**Update 4 (2026-07-07, später):** Martin-Direktive _„Beachte, dass der
background worker auch parallel multi tasking laufen kann. Also kein
fixer dateiname im temp ordner"_ → Stereo-Concatenation als Pre-Processing
vor ASR-Call. `combined-stereo.wav` wird im **Meeting-Ordner** abgelegt
(pro Task eindeutig, kein OS-Temp-Fix-Name) — keine Collision bei
parallelen Worker-Tasks. Plus Frage _„Wie wird das Transkript
zusammengefügt?"_ → **Cross-Channel-Correlation (RMS-Verhältnis)**
mappt Provider-Speaker-IDs auf Local/Remote-N. Neue Komponenten
`StereoConcatenator` + `SpeakerRoleAssigner`. Datenmodell erweitert um
`LocalSpeakerId`/`RemoteSpeakerIds`/`SpeakerRoleMap`. ~18 neue Tests
(Stereo + RMS-Correlation). **Komplett durch Update 5 ersetzt.**

**Update 3 (2026-07-07, später):** Martin-Direktive _„Beide provider
implementieren. Auswahl in settingsdialog."_ → **Beide Provider werden
parallel implementiert** (Azure Speech via SDK, Deepgram via REST),
Auswahl zur Laufzeit via `TranscriptionConfig.Provider` im neuen
Settings-Tab „Transcription". Martin bestätigt: _„V0.4 erst nach 0.3"_
→ Outlook-Kalender-Integration explizit auf nach v0.3-Abnahme verschoben.
Damit sind alle offenen Punkte geklärt.

**Update 2 (2026-07-07, später):** Martin-Direktive _„Teams Meeting Detection
soll über einen App Reader getriggert werden"_ → Architektur grundlegend
geändert. Statt separatem `IMeetingDetector` wird der bestehende
`TeamsAppReader` (Spec 0011) als Trigger-Quelle erweitert. 6 von 8 TBDs
geklärt; verbleibend: Provider-Auswahl (Azure Speech vs. Deepgram),
Kalender-Lookup (v0.4).

**Ergebnis:** `specs/0013-audio-notes-mvp3.md` als Skeleton v0.3 angelegt
(~28 KB, vollständige Sektionen + Datenmodell + Konfiguration + TDD-Plan
+ 2 verbleibende TBD-Punkte für Martin).

### Architektur-Entscheidungen

| # | Thema | Entscheidung | Begründung |
|---|-------|--------------|------------|
| 1 | Teams-Meeting-Erkennung | **Teams App Reader (Spec 0011) als Trigger-Quelle** | Martin-Direktive 2026-07-07. Spec 0011 hat bereits `TeamsChatKind.Meeting` (Enum) + `TeamsTitleInfo.IsMeeting` (Bool-Flag) + Title-Parser für `"Meeting \| Daily Standup - Microsoft Teams"`. Neues Event `MeetingStateChanged` auf `TeamsAppReader` macht `false→true`/`true→false`-Übergänge für `TriggerSupervisor` sichtbar. **Separater `IMeetingDetector`/`TeamsMeetingDetector` verworfen** — würde Parsing-Logik duplizieren. |
| 2 | Trigger-Debounce | **30 s Mindest-Meeting-Dauer, sonst verworfen** | Verhindert Aufnahme von 5-Sekunden-Test-Meetings oder schnellen Tab-Wechseln. Konfigurierbar via `appReader.teams.minMeetingDurationSeconds`. Implementierung in `AudioRecorderSession` (nicht im App Reader). |
| 3 | Audio-Encoding | **PCM 16-bit, 16 kHz, Mono, WAV-Container** | Martin-Direktive 2026-07-07 (Punkt 2: „pcm"). Whisper-Standard-Eingabe, keine Resampling-Latenz, Azure Speech + Deepgram akzeptieren beide direkt. Opus-File als Alternative verworfen (Martin-Entscheidung). |
| 4 | Container-Layout | **Zwei separate Mono-Files (`mic.wav` + `loopback.wav`)** statt Stereo-Mix | Stereo-Mix würde Diarization erschweren (welcher Speaker in welcher Spur?). Zwei separate Spuren erlauben getrennte Pre-Processing-Pipelines. Nachteil: ~doppelter Speicher (~30 MB/h). |
| 5 | Persistenz-Layout | `%APPDATA%/AiRecall/audio/yyyy-MM-dd/HHmmss-{meetingIdShort}/{mic,loopback,meta}.*` | Pro Meeting ein eigener Ordner, analog `capture/yyyy-MM-dd/`-Pattern. `meetingIdShort` = erste 8 Hex-Zeichen eines SHA256(Process + StartedAt-Tag) — deterministisch, deduplizierbar. |
| 6 | MD-Pattern | **Eine `meta.md` pro Meeting**, Frontmatter + Transcript in-place angehängt | Analog OCR-Pattern (Bug-Bash I-17, Spec 0007): Content wird in-place in die MD-Datei geschrieben, keine separaten Files. Frontmatter-Feld `transcript_status: pending\|partial\|done\|failed` als Idempotenz-Marker. |
| 7 | Audio-Devices in Settings | **Neuer Tab „Audio" im SettingsDialog** (Spec 0009-Foundation), Test-Button funktioniert **immer** (auch bei `audio.enabled=false`) | Martin-Direktive 2026-07-07 (Punkt 4: „nein — keine Prüfung"). Settings-Dialog hat bereits dynamische Tab-Generierung via `ConfigSchemaReflection` (Spec 0009 v1.0). Test-Button unabhängig von Master-Switch, damit User Devices validieren kann bevor er `audio.enabled=true` setzt. |
| 8 | Transcription-Worker | **Eigener `TranscriptionWorker` analog `ConversionWorker`** (Spec 0007-Pattern) | Gleiche Architektur: `Channel<AudioTranscriptionTask>` + Background-Task + max-N-parallel. Identisches Enqueue-/Finalize-/Idempotenz-Pattern. Neue DLL `AiRecall.Conversion.Transcription`. |
| 9 | Provider-Interface | **`ITranscriptionProvider` mit `TranscribeAsync(micPath, loopbackPath, options, progress, ct)`** | Provider-Austauschbar (Azure Speech, Deepgram). Diarization-Verpflichtung im Interface, nicht in jedem Provider. |
| 10 | Diarization | **Pflicht, nicht optional** | Martin-Direktive 2026-07-07 (implizit aus Punkt 7). Provider ohne Diarization werden vom Worker abgelehnt → `transcript_status: failed`. **Sowohl Azure Speech als auch Deepgram liefern Diarization nativ** — keine Custom-Modelle nötig. |
| 11 | Speaker-Labels | **„S1", „S2", ... (anonyme IDs)** in v0.3 | Reale Namen-Mapping als v0.4 über Outlook-Kalender-Lookup + Contact-Match (siehe Punkt 13). |
| 12 | Transcription-Provider (Auswahl) | **Beide implementiert** — Azure Speech + Deepgram parallel, Auswahl via `TranscriptionConfig.Provider` im Settings-Dialog (Tab „Transcription") | Martin-Direktive 2026-07-07 Update 3 (Punkt 1: „Beide provider implementieren. Auswahl in settingsdialog"). Azure Speech via `Microsoft.CognitiveServices.Speech` SDK, Deepgram via REST + `HttpClient`. Beide Cloud, beide mit nativer Diarization. Azure ~$1/h, Deepgram ~$0.26/h (Pay-as-you-go). Provider-Key in `TranscriptionConfig.ProviderApiKey` als Klartext in `%APPDATA%` (siehe Punkt 6). |
| 13 | Outlook-Kalender-Integration | **Ausbaustufe v0.4, nach v0.3-Abnahme** | Martin-Direktive 2026-07-07 (Punkt 7: „ausbaustufe") + Update 3 („V0.4 erst nach 0.3"). v0.3 befüllt nur `topic` (aus Title-Parser); v0.4 ergänzt `participants`, `description`, `calendar_appointment_id`, `organizer` per Outlook-COM-Suche. Architektur-Vorbereitung in v0.3 durch optional befüllbare `MeetingMetadata`-Felder in der MD-Frontmatter. |
| 14 | Stereo-Concatenation (Update 6, finale Form) | **`combined-stereo.wav` im Meeting-Ordner** (links = mic, rechts = loopback) — beide Kanäle erhalten, Diarization komplett im Provider | Martin-Direktive 2026-07-07 Update 6: „Nutze weiterhin stereo mit beiden Kanälen." Mono-Mix (Update 5) zurückgenommen. Begründung: Deepgram kann Multi-Channel-Diarization nutzen, Azure Speech downmixt intern. Beide Kanäle bleiben erhalten für v0.4 (Per-Channel-Re-Transkription, Audio-Pre-Processing). RMS-Analyse bleibt gestrichen (Update 5). Komponente `StereoConcatenator`. |
| 15 | Polling + Recording-Lifecycle (Update 8) | **Polling-basierte Anwesenheitserkennung** (`MeetingPresencePoller`, 5-s-Intervall, Edge-Detection) als alleinige v0.3-Quelle für Start/Stop. **Recording in eigenem Thread** (`RecordingSession` via `Task.Run`), Stop via `CancellationToken`. **MD-Stub-Pattern**: `meta.md` zweistufig (Start = `recording`, Stop = `recorded` mit Audio-Links), Frontmatter-Feld `worker_task_enqueued` für Recovery-Scan. | Martin-Direktive 2026-07-07 Update 8: „Es muss auch automatisch erkannt werden, wann ein Meeting endet … regelmäßig nach dem Meeting Fenster gesucht werden" + „Aufnahme in einem eigenen thread … Audio Dateien geschrieben und die md datei verlinkt beide … background worker liest diese dann später ein". Polling ist selbst-heilend, robust gegen verlorene Events (Teams-Reload, Network-Drop, UI-Crash). Neue Komponenten: `MeetingPresencePoller`, `RecordingSession`, `MetaMdLifecycleManager`, `MetaMdFrontmatter`, `MeetingPresenceStateChangedEventArgs`, `MeetingPresenceSnapshot`, `MeetingRecordingPaths`. Event-driven `MeetingStateChanged` aus Spec 0011/Update 2 bleibt im Code für v0.4-Verbesserung (schnellere Reaktion < 100 ms), wird aber in v0.3 NICHT vom `TriggerSupervisor` abonniert. Tests-Plan: ~91 → ~109 neue Tests. |

### Martin-Direktiven 2026-07-07 (Übersicht)

| # | Thema | Direktive | Auswirkung |
| - | - | - | - |
| A | Trigger-Architektur | „über einen App Reader" | Teams App Reader (Spec 0011) erweitern um `MeetingStateChanged`-Event; separater Detector verworfen |
| B | Provider | „azure speech oder deepgram" | WhisperX/faster-whisper/OpenAI raus aus Spec; Kandidaten-Liste auf 2 Cloud-Provider |
| C | Encoding | „pcm" | Opus als Alternative gestrichen; PCM-16-WAV Mono 16 kHz fix |
| D | Off-Hours | „immer" | Kein Nacht/Wochenende-Skip |
| E | Laptop-Mode | „nein — keine Prüfung" | Kein Battery-Aware-Recording |
| F | Disk-Quota | „nein — keine Prüfung" | Keine Auto-Rotation; User-Verantwortung |
| G | Encryption | „nein, keine Verschlüsselung" | OS-Bitlocker/EFS ausreichend; kein App-seitiges Crypto |
| H | Kalender-Integration | „Ausbaustufe" | v0.4-Spec folgt; v0.3 nur Topic |
| I | Trigger-Robustheit | „erst mal egal" | Teams-Reload/Network-Drop = Recording stoppt; kein Re-Init in v0.3 |
| J | Provider-Implementierung (Update 3) | „Beide provider implementieren" | Azure Speech + Deepgram parallel implementiert; Auswahl via Settings-Dialog |
| K | v0.4 Roadmap (Update 3) | „V0.4 erst nach 0.3" | Outlook-Kalender-Integration explizit nach v0.3-Abnahme verschoben, nicht parallel |
| L | Concurrency / Multi-Task (Update 4) | „parallel multi tasking … kein fixer dateiname im temp ordner" | `combined-stereo.wav` im Meeting-Ordner (nicht OS-Temp); pro Task eindeutig, keine Collision |
| M | Diarization (Update 5) | „Streiche das rms. Diarization macht der Provider" | RMS-Cross-Channel-Correlation komplett raus; Diarization läuft im Provider, wir geben rohe Speaker-IDs (S0, S1, S2) weiter |
| N | Stereo erhalten (Update 6) | „Nutze weiterhin stereo mit beiden Kanälen" | Mono-Mix aus Update 5 zurückgenommen; Stereo-Concatenation mit beiden Kanälen als finaler Pre-Processing-Schritt |
| O | Azure Speech auch mit Stereo (Update 7) | „Azure speech auch mit stereo nutzen" | Azure Speech verarbeitet Stereo nativ (kein interner Downmix); Response enthält `ChannelId` zusätzlich zu `SpeakerId`. Beide Provider bekommen das gleiche `combined-stereo.wav` |
| P | Meeting-Ende-Erkennung + Recording-Lifecycle (Update 8) | „Es muss auch automatisch erkannt werden, wann ein Meeting endet, um die Aufnahme zu stoppen … Um das Meeting ende zu erkennen muss regelmäßig nach dem Meeting Fenster gesucht werden" + „Generell sollte die Aufnahme in einem eigenen thread laufen, bis das stopp Signal kommt. Dann werden die Audio Dateien geschrieben und die md datei verlinkt beide. Der background worker liest diese dann später ein und startet das transkript" | **Polling-basierte Anwesenheitserkennung** (`MeetingPresencePoller`, 5-s-Intervall, Edge-Detection) als alleinige Quelle für Start/Stop in v0.3; Event-driven `MeetingStateChanged` aus Spec 0011/Update 2 bleibt im Code, wird aber NICHT vom `TriggerSupervisor` abonniert. **Recording-Lifecycle**: Aufnahme läuft in eigenem Thread (`RecordingSession` via `Task.Run`), Stop via `CancellationToken`, kein Thread.Abort. Beim Stop: Buffer-Snapshots → `mic.wav` + `loopback.wav` schreiben → `meta.md` Status `recorded` mit `audio_files`-Links setzen → Live-Enqueue im Worker. **MD-Stub-Pattern**: `meta.md` zweistufig geschrieben (Start = `recording`, Stop = `recorded`), Frontmatter-Feld `worker_task_enqueued` für Crash-Recovery-Scan. Neue Komponenten: `MeetingPresencePoller`, `RecordingSession`, `MetaMdLifecycleManager`. |

### Verworfen / Out-of-Scope v0.3

- **IMeetingDetector / TeamsMeetingDetector als separate Komponente** — Martin-Direktive
  Trigger via App Reader. Architektur erlaubt späteres Hinzufügen via weiterem App Reader
  (z. B. `ZoomAppReader` für Zoom-Meetings).
- **Andere Meeting-Apps** (Zoom, Discord, Webex, Slack Huddles) — Folge-Cluster.
- **Stereo-Mix-Container** — Diarization-Pipeline braucht separate Spuren.
- **Live-Transkription während Meeting** — nur Post-Meeting in v0.3.
- **Multi-Language Auto-Detect** — Default-Sprache pro Recording (`TranscriptionConfig.DefaultLanguage`).
- **Speaker-Mapping auf reale Namen** — v0.4 (über Outlook-Kalender + Contact-Match).
- **Audio-Pre-Processing** (Noise-Suppression, AGC, Echo-Cancellation) — v0.4.
- **Aufnahme-Indikator** (Tray-LED, Animation) — nice-to-have, nicht MVP.
- **Reine Audio-Spike-Detection** für Trigger — zu viele False-Positives.
- **Opus-Encoding** — Martin hat PCM final entschieden.
- **Lokale Whisper-Modelle (WhisperX, faster-whisper)** — Martin hat Cloud-only entschieden.
- **OpenAI Whisper API** — scheidet aus, da kein Diarization.

### Offene Punkte für Martin (verbleibend)

**Keine offenen Punkte mehr.** Alle Martin-Direktiven 2026-07-07 (Update 1-4) sind in Spec 0013 v0.3 angewandt:

| # | Direktive | Status |
| - | - | - |
| A | Trigger via App Reader | ✅ Spec 0011-Erweiterung dokumentiert |
| B | Provider: Azure oder Deepgram | ✅ **Beide implementiert**, Auswahl via Settings |
| C | Encoding: PCM | ✅ PCM-16-WAV Mono 16 kHz fix |
| D | Off-Hours: immer | ✅ Kein Nacht/Wochenende-Skip |
| E | Laptop-Mode: keine Prüfung | ✅ Kein Battery-Aware-Check |
| F | Disk-Quota: keine Prüfung | ✅ Keine Auto-Rotation |
| G | Encryption: keine | ✅ OS-Bitlocker/EFS ausreichend |
| H | Kalender: Ausbaustufe | ✅ v0.4 nach v0.3-Abnahme (Martin-Direktive Update 3) |
| I | Trigger-Robustheit: egal | ✅ Teams-Reload stoppt Recording, kein Re-Init in v0.3 |
| J | Beide Provider implementieren | ✅ Azure Speech + Deepgram parallel |
| K | v0.4 nach v0.3 | ✅ Explizit nach v0.3-Abnahme, nicht parallel |
| L | Multi-Task Concurrency (Update 4) | ✅ Stereo-File im Meeting-Ordner, nicht OS-Temp |
| M | Diarization (Update 5) | ✅ RMS ersatzlos gestrichen, Provider macht Diarization, rohe Speaker-IDs |
| N | Stereo mit beiden Kanälen (Update 6) | ✅ Stereo-Concatenation final, Mono-Mix aus Update 5 zurückgenommen |
| O | Azure Speech auch Stereo (Update 7) | ✅ Beide Provider bekommen gleiches Stereo-File, Azure nativ ohne Downmix |
| P | Event-driven Meeting-Detection als v0.3-Trigger (Update 8) | ✅ Polling-only in v0.3, Event-driven `MeetingStateChanged` bleibt im Code für v0.4 (schnellere Reaktion < 100 ms), Polling als selbst-heilender Fallback |

### Folge-Cluster (v0.4+, erst nach v0.3-Abnahme — Martin-Direktive Update 3)

- **Outlook-Kalender-Integration** (Martin-Direktive 2026-07-07): Suche nach Outlook-`Appointment` mit `IsOnlineMeeting==true` und `Start <= DetectedAt <= End`; Metadaten (Participants, Description, CalendarAppointmentId, Organizer) in laufende `meta.md` nachtragen. **Wird erst nach v0.3-Abnahme begonnen** (Martin: „V0.4 erst nach 0.3").
- Speaker-Mapping auf reale Namen (über Outlook-Kalender + Contact-Match)
- Audio-Pre-Processing (Noise-Suppression, AGC, Echo-Cancellation via WebRTC-Audio-Processing-Library)
- Multi-Meeting-Recording (mehrere Teams-Accounts gleichzeitig)
- Auto-Wiki MVP 4 (auf Audio-Notes-Infrastruktur aufbauend)

### Tests

- TDD-Plan in Spec 0013 §Tests: ~91 neue Tests (Ziel: 674 → ~765)
- AudioRecorder (15), AudioDeviceProvider (8),
  **Teams-App-Reader-Erweiterung** (8, ersetzt MeetingDetector-Tests),
  **TriggerSupervisor-Audio-Wiring** (6, neu), TranscriptionWorker (12),
  Provider-Stub (6), MD-Generator (8), Settings-Audio-Tab (5)

### Verwandte Specs

- `specs/0005-trigger-pipeline.md` — TriggerEvent-Infrastruktur (wiederverwendet)
- `specs/0007-async-conversion.md` — ConversionWorker-Pattern (1:1 übernommen)
- `specs/0009-settings-dialog.md` — Settings-Tab-Foundation (AudioConfig-POCOs)
- `specs/0011-teams-app-reader.md` — **Trigger-Quelle** (TeamsChatKind.Meeting + IsMeeting + neues MeetingStateChanged-Event)
- `specs/0012-tessdata-first-run.md` — Modal-Dialog-Stil (nicht direkt relevant)

---

## 2026-07-05 — Teams App-Reader (Spec 0011)

Spec 0011 ist mit Commits `ec4631e` ... `237b457` abgeschlossen (Cluster 1–5;
Cluster 6 = dieser Doku-Eintrag). Test-Count 589 → **650/650 grün** (+61).
Modul komplett neu in `AiRecall.AppReader.Teams` (4 Files, 0 Warnings, 0 Errors).

**Direktive Martin 2026-07-05:** „Nur das neue Teams — nicht legacy, nicht graph."
Verbindlich für Spec 0011: Modern Teams (Electron, `ms-teams.exe`) + UIA + CDP opt-in.
Legacy Teams Classic (`Microsoft Teams` ProgID COM) und Graph API (OAuth) explizit
ausgeschlossen.

| # | Thema | Entscheidung | Begründung |
|---|---|---|---|
| 1 | DLL-Struktur | **Neue DLL `AiRecall.AppReader.Teams.dll`** (analog Outlook/OneNote) | Eine DLL pro App-Familie. Plugin-Discovery via `AppReaderRegistry` automatisch. `UseWPF=true` für UIA + WebSocket. |
| 2 | Architektur-Modus | **Read-only (kein `OnPoll`)** | Modern Teams synchronisiert serverseitig, lokal sichtbar = was User sieht. Read reicht. `SupportsBackgroundPolling = false`. `PollIntervalSeconds = 0` im Default. |
| 3 | Active-Chat-Strategie | **3-stufige Kette: CDP → UIA → Title-Fallback** | CDP liefert reichhaltigen Chat-Content (HTML→MD, mit Reply-Threads, Sender-Highlighting), erfordert aber User-Mitarbeit (Teams mit `--remote-debugging-port` starten). UIA ist immer verfügbar, aber Plain-Text. Title-Fallback wenn beides scheitert. Konfigurierbar via `TeamsConfig.PreferredStrategy` (Cdp / Uia / Auto-Default). |
| 4 | CDP-Discovery | **HTTP-GET auf `/json/version` + `ClientWebSocket` zu `webSocketDebuggerUrl`** | Built-in `System.Net.WebSockets` aus .NET 8 — kein NuGet-Paket, keine externe Library. Discovery-Failure (kein aktiver Debug-Server) → Fallback auf UIA, kein Hard-Fail. |
| 5 | CDP-Timeout | **`CdpTimeoutMs` (Default 1500) via `CancellationTokenSource.CancelAfter(...)`** | Schutz gegen hängende WebSocket-Connections. Bei Timeout → Fallback auf UIA. |
| 6 | UIA-Implementation | **Window-Title-Parser für Modern-Teams-Format** | Format: `"Chat | Alice - Microsoft Teams"` (1:1), `"Channel | #general - Microsoft Teams"` (Channel), `"Group Chat | Project Alpha - Microsoft Teams"` (Group), `"Meeting | Daily Standup - Microsoft Teams"` (Meeting). Zerlegt nach `|` als Trenner, `- Microsoft Teams` als Suffix. Edge-Case: kein Separator → `ChatName = title.Trim()`, `Suffix = ""`. |
| 7 | Sender-Separation | **Heuristik im UIA-Pfad** (Plain-Text → Liste von `(Sender, Body)`) | UIA liefert nur geflashten Text ohne Struktur. Heuristik: Zeile mit Zeit-Prefix (`HH:mm` oder `Yesterday`) + nachfolgender Name → Message-Boundary. Reply-Threads fehlen in Iter. 1, CDP-Pfad liefert sie in Iter. 2. |
| 8 | ChatID-Hash | **`ComputeChatId(Title, Type, SenderSet)` = SHA256 → erste 8 Hex-Zeichen** | Deterministisch: zwei Captures mit identischer Chat-Konstellation (gleicher Chat + gleiche Senders) → identischer `chatIdShort` → spätere Deduplikation möglich. Edge-Case `""` → `"0"` (defensiv, testbar). |
| 9 | Persistenz-Schema | **`capture/yyyy-MM-dd/ms-teams/HHmmss-{chatIdShort}.md`** | Analog Outlook/OneNote. `chatIdShort` ersetzt die bei Teams nicht verfügbare eindeutige Message-ID. YAML-Frontmatter mit `kind=teams-chat`, `chatType`, `chatTitle`, `chatIdShort`, `source=teams-cdp|teams-uia|teams-title-fallback`, `strategy`, `senderCount`, `messageCount`, `isSelfIncluded`, `capturedAt`, `reader`, `readerVersion`. |
| 10 | Title-Fallback-Body | **`(teams content unavailable, only title captured)`** | Klare Markierung im MD, dass nur der Title persistiert wurde. Verhindert Verwechslung mit leerem Chat. |
| 11 | `TeamsContent` + `TeamsTitleInfo` als `public` | **`public sealed record`/`record`**, nicht internal | C# CS0051: `internal` types in `public` method signatures erzeugen Inconsistent-Accessibility-Error. Da `ParseWindowTitle` als `public static` exponiert wird (via `TeamsUiaReader`), müssen `TeamsTitleInfo`/`TeamsChatKind` ebenfalls `public` sein. |
| 12 | `ChatTitleHint` als `init`-Property | **`public string ChatTitleHint { get; init; }`** | Erlaubt Set im Record-Konstruktor + `with { ChatTitleHint = ... }`, blockiert externe Mutation. |
| 13 | `SkipChatPatterns` + `IncludeSenderPatterns` | **Case-insensitive Substring-Filter** (Listen, User-konfigurierbar) | `SkipChatPatterns` filtert Chats anhand des Title (z. B. „Bots", „Notifications"). `IncludeSenderPatterns` = Whitelist (leer = alle Sender). Beide im Frontmatter dokumentiert, nicht im MD-Body. |
| 14 | Test-Strategie | **Pure-Function-First, Read-Pfad nicht unit-testbar** (ohne installiertes Teams + CDP-Endpoint) | `ParseWindowTitle`, `IsTeamsChatWindow`, `ChatIdShort`, `BuildFullMarkdown` voll testbar. `Read()` mit CDP-Endpoint als `[Trait("Integration", "Teams")]` markiert — auf Martins Workstation lauffähig, in CI geskippt. `TryFindEndpoint` mit Mock-`HttpMessageHandler` testbar. |
| 15 | CDP-Mock-Pattern | **HttpMessageHandler-Subclass** statt WireMock oder externer Library | Minimaler Overhead, vollständig in-process testbar. Mock liefert `/json/version`-Response mit `webSocketDebuggerUrl`; Test simuliert WebSocket-Connect ohne echten Server. |
| 16 | `Microsoft.Web.WebView2` Dependency | **NICHT erforderlich** | `System.Net.WebSockets` ist built-in in .NET 8 und reicht für CDP-Client. WebView2 wäre für ein Browser-Embedding, nicht für ein DevTools-Client. |

### Tests

- 61 neue Tests in 4 Files: `TeamsConfigTests` (5), `TeamsUiaReaderTests` (29 = 9 Facts + 4 Theories mit 20 InlineData), `TeamsCdpReaderTests` (10), `TeamsAppReaderTests` (17 = 10 Facts + 1 Theory mit 7 InlineData).
- Test-Count gesamt: **650/650 grün** (vorher 589).
- Teams-Modul: **0 Warnings, 0 Errors**.
- Cluster: Spec + Config (Cluster 1, `ec4631e`) → UiaReader + Skeleton (Cluster 2, `678c8bd`) → CdpReader (Cluster 3, `d7eec32`) → AppReader (Cluster 4, `95d0a49`) → Tests (Cluster 5, `237b457`) — 5 thematische Commits + Docs-Update (Cluster 6, dieser Eintrag).

### Lessons Learned (Cluster 1–5)

- **C# CS0051 — `internal` types in `public` method signatures**: Lösung ist `public` record/enum für die Typen, die in `public` Methoden-Signaturen auftauchen. Records und Enums, die in `public` API exponiert werden, müssen `public` sein, auch wenn sie nur intern genutzt werden.
- **`init`-Accessormodifier** auf Records für schreibgeschützten Test-State (`CS8856` Fix).
- **Title-Parser Edge-Case** „kein Separator": erst `IndexOf(" | ")` suchen, wenn `-1` → `ChatName = title.Trim()` ohne Separator-Injection. Expliziter Test deckt den Edge-Case dauerhaft ab.
- **`ChatIdShort("")` → `"0"`** als defensive Edge-Case-Behandlung (verhindert kryptische Filename-Kollisionen mit deterministischem SHA256-Output).
- **xUnit `Record.ExceptionAsync` mit `async () => return await ...`-Lambda** funktioniert nicht zuverlässig für saubere Stack-Traces → try/catch direkt im Test.
- **CSP-Search für Test-Pfade** `AppContext.BaseDirectory` Walk-Up (`AiRecall.sln` suchen statt fixer `../../../../../..`-Navigation) — robust gegen CI vs. lokales net8.0 vs. net8.0-windows.
- **`UseWPF=true` im csproj** für UIA + WebSocket-Client (analog Documents-Reader, Iter. 2).

### Verworfen

- **Legacy Teams Classic** (`Microsoft Teams` ProgID COM): seit 2023 deprecated. Martin-Direktive 2026-07-05: „Nur das neue Teams — nicht legacy, nicht graph."
- **Graph API** (REST/OAuth): async-Parallelität, Token-Lifecycle, OnPoll-Pattern passt nicht zum synchronen `IAppReader.Read()`. Out-of-Scope, wäre Spec 0014.
- **Tab-Wechsel-Erkennung** während Capture: pro Read-Call nur das aktuell aktive Tab, Multi-Tab-Chats = Multi-Captures (deterministisch via ChatID-Hash deduplizierbar).
- **Reply-Threads-Struktur im UIA-Pfad**: UIA liefert nur geflashten Text, keine Hierarchie. CDP-Pfad mit DOM-Analyse könnte Threads in Iter. 2 liefern.
- **Inline-Media-Persistierung** (Bilder, GIFs, Voice Messages): UIA liefert nur Alt-Text, CDP liefert DOM-URLs. Beide in Plain-Text/MD, keine File-Persistierung.
- **Reactions/Emojis**: Iter. 2 via CDP-DOM-Analyse.
- **File-Attachments aus Teams-Chats**: nicht im Scope.
- **Eigener WebSocket-Pool**: einmalige Connection pro Read reicht; bei nächstem Read wird ohnehin ein neuer Reader gebaut.

### Auswirkungen

- Neue DLL: `AiRecall.AppReader.Teams` (ProjectRef → `AiRecall.AppReader.Base` + `AiRecall.Core`)
- Neue Config-Sektion `appReader.teams`: `Enabled`, `MaxContentKB`, `UseCdpIfAvailable`, `CdpEndpoint`, `CdpTimeoutMs`, `PreferredStrategy`, `PollIntervalSeconds`, `SkipChatPatterns`, `IncludeSenderPatterns`
- Neuer Persistenz-Subdir: `capture/yyyy-MM-dd/ms-teams/`
- `AppReaderRegistry` pickt `AiRecall.AppReader.Teams.dll` automatisch via Reflection auf (`AppReaderRegistry.LoadFromDirectory`-Pattern)
- `AiRecall.sln` um neue Project-Reference erweitert
- `AiRecall.Core.Tests.csproj` um `<ProjectReference>` auf Teams-Modul erweitert
- Commits: `ec4631e` (Spec+Config), `678c8bd` (UiaReader+Skeleton), `d7eec32` (CdpReader), `95d0a49` (AppReader), `237b457` (Tests), `(TBD)` (Cluster 6 = Doku)

---

## 2026-07-05 — OneNote App-Reader (Spec 0010)

OneNote als dritte COM-bindungende App-Familie (nach Outlook + Documents).
Im Gegensatz zu Outlook (Dual-Modus mit OnPoll) ist OneNote Page-orientiert
und braucht keinen Background-Stream. Pattern analog Outlook App-Reader,
aber strukturell vereinfacht (Read-only).

| # | Thema | Entscheidung | Begründung |
|---|---|---|---|
| 1 | Lese-Strategie | **4-stufige Active-Page-Strategie**: Stage 1 `Windows.CurrentWindow.CurrentPageId`, Stage 2 `Windows`-foreach + `Active`, Stage 3 `GetHierarchy(hsPages)` + XPath `isCurrentlyViewed="true"`, Stage 4 null | Microsoft-dokumentierte API für OneNote 2013+. Wenn `CurrentWindow` nicht verfügbar (Edge-Cases), iterate Collection. Wenn COM-Hierarchie scheitert, parse `isCurrentlyViewed`-Filter. Bei allem null → Caller fällt auf OCR zurück. Konfigurierbar via `OneNoteConfig.ActivePageStrategy` (WindowsApi / HierarchyXml / Auto). |
| 2 | Architektur-Modus | **Read-only (kein `OnPoll`)** — Capture ausschliesslich via Trigger (Foreground/Heartbeat) | OneNote arbeitet mit Einer sichtbarer Page. Kein konstanter Daten-Stream im Hintergrund. `SupportsBackgroundPolling = false` (anders als Outlook Dual-Modus mit Polling). `PollIntervalSeconds = 0` im Default. |
| 3 | COM-Strategie | **Late binding via ProgID `OneNote.Application`** + P/Invoke auf `oleaut32.dll!GetActiveObject` | Analoger Workaround wie Outlook + Documents. `Marshal.GetActiveObject` ist im .NET 8 SDK 8.0.422 nicht direkt verfügbar. P/Invoke ist robust, kein NuGet-Paket, keine PIAs. |
| 4 | Retry-Logik | **3× Retry mit 500ms Backoff**, transient-HRESULTs retry, fatal-HRESULTs kein Retry | OneNote COM ist fragil. `OneNoteComException` klassifiziert nach `IsRetryable`: fatal = `hrXmlIsInvalid` (0x80042001, fehlerhaftes XML-Schema) + `hrRpcFailed2` (0x800706BA, RPC-Server-Crash); transient = `hrRpcUnavailable`/`hrCOMBusy`/`hrServerCallRetried`/`hrObjectMissing`. Retryable-Errors lösen `Thread.Sleep(500ms)` zwischen Versuchen aus. Quelle: OneMore-AddIn-Production-Pattern. |
| 5 | RCW-Cleanup | **Separate `object?`-Variablen + `Marshal.ReleaseComObject` in finally-Blocks** + `ReferenceEquals`-Schutz gegen Doppel-Release | Outlook-Pattern. Jede COM-Stufe (App, Window, Hierarchy) bekommt eigene Variable + finally-Block. Bei `Stage 3`-Fallback werden möglicherweise alle Stages durchlaufen — ReferenceEquals-Schutz verhindert, dass `currentWindow` doppelt released wird (sowohl in Stage 1-Branch als auch in finally). |
| 6 | XML-Schema-Konstante | **`xs2013` immer** (nicht `xsCurrent`) | OneNote-API hat `xsCurrent`, das je nach Office-/OneNote-Version unterschiedliche Schemas liefert. `xs2013` ist stabil, gut dokumentiert, von OneMore & Co produktiv verwendet. Schema-Konstante als `private const` in `OneNoteComInterop`, im Helper klar dokumentiert. |
| 7 | XML→MD als Pure-Function | **`OneNotePageXmlToMarkdown.ConvertBody(xml, cfg)` ist zustandslos, IO-frei, deterministisch** | Vollständig unit-testbar ohne OneNote-Installation. 30 Tests in `OneNotePageXmlToMarkdownTests` decken `one:OE`/`T`/`Image`/`Tag`/`Table`/`InkContent`/`InsertedFile`/Bullet-Indent/HTML-Entities/Edge-Cases ab. Pattern analog `OutlookBodyToMarkdown` (Spec 0004 Iter. 3). |
| 8 | Mapping `one:OE` | **Inline-Content + Sub-Bullets rekursiv mit `append + newline`-Pattern** | OneNote-XML erlaubt mehrere `one:T`-Runs in einem `one:OE` (gestylte Text-Fragmente). Sub-OEs = Sub-Bullets. `AppendOE` konkatentiert Inline-Content in StringBuilder, ruft sich für Sub-OEs rekursiv mit `listIndent + 1` auf. |
| 9 | Bullet-Heuristik | **`OE style="list..."` ist Bullet, sonst plain paragraph** | OneNote speichert List-Style im `style`-Attribut (z. B. `style="list:Bullet"`). Wir matchen Substring `list` case-insensitive, Prefix `- ` (mit 2-Space-Indent pro Ebene). Kein Bullet → plain paragraph ohne Prefix. |
| 10 | Image-Include Default | **`IncludeImages = false`** (Default) | Base64-Inflation in MD-Files (1 MB PNG → 1.3 MB MD), Datenschutz-Risiko bei handschriftlichen Skizzen. Aktivieren nur bei expliziter Konfiguration. Flag in `OneNoteConfig` + JSON-Section in `default-config.json`. |
| 11 | Tag-Format | **`to-do:empty` → `[ ]`, `to-do:complete` → `[x]`, custom-Tag → `#tag`** | OneNote speichert To-Do-Status im `type`-Attribut des `<one:Tag>`-Elements. Wir matchen `to-do`-Prefix + `complete`-Suffix. Custom-Tags werden als `#tag-name` (fett) gerendert. Inline-Format im OE-Context. |
| 12 | InkContent-Handling | **`*(handschriftlich)*` Hinweis + optional OCR-Text aus `<one:InkWord>`/<one:RecognizedText>`** | Handschrift kann nicht visuell in MD abgebildet werden. Wenn OneNote Handschrift via OCR erkannt hat, wird der Text in derselben Zeile angehängt. |
| 13 | Persistenz-Schema | **`capture/yyyy-MM-dd/onenote/HHmmss-{pageIdShort}.md`** | Analog Outlook (`outlook-mail`-Subfolder). PageIdShort = erste 8 Zeichen der Page-GUID ohne Bindestriche (z. B. `AB12CD34`). Pattern in `OneNoteHierarchyInfo.PageIdShort`-Property berechnet. |
| 14 | YAML-Frontmatter | **`HierarchyDepth`-konfigurierbar**: PageOnly / PageAndSection (Default) / PageAndSectionAndNotebook | Wahl zwischen Datenschutz (PageOnly) und Vollständigkeit (alle drei Ebenen). Default = PageAndSection (Notebook ohne Privacy-Sorgen, falls Titel persönliche Daten enthält). Toggle via `OneNoteConfig.HierarchyDepth`. |
| 15 | Test-Injection | **`internal OneNoteAppReader(ILogger logger, string captureRoot)`** + `[InternalsVisibleTo("AiRecall.Core.Tests")]` | Pattern analog OutlookAppReader. Tests injizieren Logger (`NullSink`) und Capture-Root (Temp-Directory) ohne installiertes OneNote und ohne `%APPDATA%`. Production: parameterloser `OneNoteAppReader()`-Konstruktor für `Activator.CreateInstance` im Plugin-Loader. |
| 16 | Test-Strategie | **Pure-Function-First, App-Reader-Read-Pfad nicht unit-testbar** (ohne installiertes OneNote) | Pure-Function-Helper (`ParseIsCurrentlyViewed`, `ParseSelfHierarchyXml`, `ConvertBody`, `BuildFullMarkdown`) vollständig testbar. Der `Read()`-Pfad benötigt COM, markiert mit `[Trait("Integration", "OneNote")]` für spätere Martins-Workstation-Smoke-Tests (analog Outlook). Auf CI wird der Trait geskippt. |

### Tests

- 64 neue Tests (`OneNoteConfigTests` 5, `OneNoteComInteropTests` 8, `OneNotePageXmlToMarkdownTests` 30, `OneNoteAppReaderTests` 21).
- Test-Count gesamt: 589 / 589 grün (vorher 525 / 525).
- Cluster: Spec + Config (Cluster 1) → ComInterop (Cluster 2) → XML→MD (Cluster 3) → Reader (Cluster 4) → Tests (Cluster 5) — 5 thematische Commits + Docs-Update (Cluster 6, dieser Eintrag).
- Commits: `c02d861`, `fd03b7b`, `ce10dec`, `1081ece`, `b8a3e20`.

### Lessons Learned (Cluster 1–5)

- **`AppContext.BaseDirectory` Walk-Up** für Test-Pfade (`AiRecall.sln` suchen statt `../../../../../..`-Relative-Navigation) — robust gegen CI vs. lokal Path-Diff (net8.0 vs. net8.0-windows).
- **`new`-Keyword für `HResult`-Shadowing** in `OneNoteComException` (`Exception.HResult` verdeckt eigene Property ohne `new`-Keyword → CS0108).
- **`HttpUtility.HtmlDecode` als built-in in .NET 8** (unter `System.Web` namespace) — kein zusätzliches NuGet für HTML-Entities nötig.
- **Bullet-Indent mit `style` attribute substring-match** — keine XPath-Query nötig, einfacher `Contains` reicht für die OneNote-Pattern (`style="list:Bullet"`).

### Verworfen

- **Last-Modified-Heuristik (Option B)**: Verworfen zugunsten `Windows.CurrentWindow.CurrentPageId` (offizielle Microsoft-API) und `isCurrentlyViewed="true"`-Fallback (entdeckt via OneMore-AddIn-Source). Heuristik wäre ungenauer und erfordert mehr Code-Pfade.
- **`OneNote UWP`-Support**: OneNote UWP hat kein COM-Interface, läuft sandboxed. Würde WinRT-API erfordern, deutlich mehr Komplexität. Out-of-Scope für Iter. 1.
- **Handschrift-OCR**: OneNote hat eigenen OCR für `one:InkContent`, aber wir vertrauen auf OneNotes OCR-Ergebnis, statt eigenen OCR drüberzulaufen. `<one:RecognizedText>`-Elemente werden inline ausgelesen.
- **Schreibzugriff (`UpdatePageContent`)**: OneNote-Page-Mutation ist nicht im Scope — wir lesen nur, niemals schreiben. UpdatePageContent-API ist zwar verfügbar, aber würde Trigger-Mode (User-confirms-before-write) erfordern, deutlich komplexer.

---

## 2026-07-04 — Trigger-Pipeline: Implementation-Resultat + nachträgliche Entscheidungen

Spec 0005 (Trigger-Pipeline) ist mit Commits `791161a` … `5d934dc`
abgeschlossen. Die folgenden Entscheidungen wurden während der
Implementation getroffen (teils Martin-Review-Fixes, teils
technische Notwendigkeiten):

| # | Thema | Entscheidung | Begründung |
|---|---|---|---|
| 1 | Assembly-Struktur | **`AiRecall.Trigger.dll` als eigenes Projekt** | MVP2-Tray-Icon-EXE soll Trigger-Code wiederverwenden, braucht aber kein ScreenCapture. Zyklusfreie Ref-Kette: Core → AppReader.Base → ScreenCapture → Trigger → Cli. |
| 2 | Trigger-Lifecycle | **`ITriggerService`-Interface + `TriggerService`-Implementierung** | Ermöglicht MVP2-Tray-EXE, denselben Code zu nutzen. `Start`/`Stop`/`Dispose` idempotent. Counter-Properties (`CaptureCount`, `ThrottleCount`, ...) für IPC. |
| 3 | Generisches Throttling | **`Throttle<TKey> where TKey : notnull`** statt separater `ThrottleIntPtr`/`ThrottleString` | DRY, ein Code-Pfad für `Throttle<IntPtr>` (HWND) und `Throttle<string>` (Prozessname). |
| 4 | HWND-Dedup | **`HwndDedup` als eigene Klasse** (nicht in `Dedup` integriert) | HWND-Key muss als Hex-String (`0xDEADBEEF`) in JSON persistiert werden, weil `IntPtr` nicht direkt serialisierbar ist. Dedup nach Prozessname nutzt weiterhin die generische `Dedup`-Klasse. |
| 5 | Channel-Topologie | **`Channel<TriggerEvent>` unbounded, SingleReader (Worker), MultiWriter (WinEventHook + Heartbeat)** | WinEventHook + Heartbeat schreiben parallel, Worker liest sequenziell. Unbounded, damit keine Events verloren gehen (Worker ist schnell genug). |
| 6 | Modal-Dialog-Strategie | **Option (a) — nur Foreground-Capture, Parent-Context als Frontmatter** (Martin-Diskussion 2026-07-04) | Beim modalen Dialog (z. B. Outlook „Neue Nachricht") wird nur das Vordergrund-Fenster aufgenommen, aber `parentHwnd`/`parentTitle`/`parentProcess` werden im YAML-Frontmatter emittiert. Erkennung via `GetAncestor(GA_ROOTOWNER) != rootHwnd`. |
| 7 | Selection-Event | **`EVENT_OBJECT_SELECTION` ist NICHT in den Trigger-Quellen** (Martin 2026-07-04) | Würde bei reinem Caret-Wechsel innerhalb desselben Inhalts Captures auslösen — zu viel Rauschen. Nur Fokus/Name/Value/Scroll sind sinnvolle Trigger. |
| 8 | CLI-Headless-Mode | **`--headless`-Flag** für MVP2-Tray-EXE und CI | Unterdrückt Console-Stats-Output, schreibt nur nach Serilog. Serilog-Output kann von Tray-EXE / NSSM / systemd-logind ausgewertet werden. |
| 9 | CLI-Trigger-Mode | **`--trigger-mode=events\|polling\|both`** (Default: events) | Tests ohne Message-Loop (z. B. Sandbox) können `--trigger-mode=polling` nutzen. Production-Default ist `events` (sparsam). |
| 10 | `recall status` | **Neuer Diagnose-Subcommand** | Liest nur von Disk (Config, heutige Captures nach Prozess, aktive Trigger-Config). `--json` für MVP2-IPC. Vorbereitung: Tray-EXE aktualisiert periodisch eine Status-Datei, die `recall status` anzeigt. |
| 11 | Alte Polling-Pipeline | **`CapturePipeline` + `EventDetector` + `Models.cs` entfernt** | War Dead-Code nach Umstellung auf `TriggerService`. Reduziert Code-Maintenance-Burden. |

### Tests

- 91 neue Tests (Schritte A–G) in `tests/AiRecall.Core.Tests/Trigger/`
  und `tests/AiRecall.Core.Tests/Persistence/`.
- Test-Count gesamt: 189 / 189 grün (vorher 98 / 98).

---

## 2026-07-04 — Documents App-Reader (Spec 0004 Iter. Documents)

Word/Excel/PowerPoint-Reader als eigenständige DLL (`AiRecall.AppReader.Documents`).
Strategie: **UIA statt COM**. Begründung + Entscheidungen:

| # | Thema | Entscheidung | Begründung |
|---|---|---|---|
| 1 | Lese-Strategie | **UIA (`System.Windows.Automation`)** statt COM-Interop | COM würde installiertes Office voraussetzen. UIA läuft auch ohne Office, liefert aber nur sichtbaren Inhalt (siehe Punkt 4/5/6). |
| 2 | Assembly-Struktur | **Neue DLL `AiRecall.AppReader.Documents.dll`** (analog zu Browser/Explorer/Notepad) | Eine DLL pro App-Familie — prozessspezifische Logik isoliert, AppReaderRegistry lädt sie automatisch beim Start neben der Exe. |
| 3 | Konfiguration | **`DocumentsConfig` mit `maxTextKB` (Default 64) + `enableUiaExtraction` (Default true)** | maxTextKB analog zu `notepad.maxBufferKB`. enableUiaExtraction erlaubt das Abschalten, falls UIA Probleme macht (dann Title-only). |
| 4 | Word-Spezifika | **Filename-Parsing statt ActiveDocument** | UIA liefert kein `ActiveDocument.Path`. Window-Titel-Format `"Doc.docx - Word"` ist gut dokumentiert und wird robust geparst (Suffix → Flags in beliebiger Reihenfolge → Unsaved-Marker → Untitled-Erkennung „Document1"). |
| 5 | Excel-Spezifika | **Hinweis im MD, dass UIA nur sichtbare Zellen liefert** | Echter Sheet-Inhalt (alle Zellen) erfordert COM. Wir liefern, was sichtbar ist, und dokumentieren die Einschränkung im Output-Markdown. |
| 6 | PowerPoint-Spezifika | **Hinweis im MD, dass UIA nur sichtbare Slide liefert** | Folien-Nummern, Notizen, Layouts erfordern COM. Wir liefern sichtbaren Inhalt + dokumentieren die Einschränkung. |
| 7 | UIA-Verfügbarkeit | **`UseWPF=true` im csproj** | UIA (`AutomationElement`, `TextPattern`, `ValuePattern`) lebt in `UIAutomationClient.dll`, das in .NET 8 nur via `<UseWPF>` automatisch referenziert wird. Alternative wäre explizite `<Reference>`-Tags, die aber in .NET 8 SDK nicht aufgelöst werden können. |
| 8 | Tests | **54 neue Unit-Tests** (`WordAppReaderTests` 18, `ExcelAppReaderTests` 14, `PowerPointAppReaderTests` 14, weitere Smoke-Tests) | Tests prüfen ParseTitle (Normal, Untitled, ReadOnly, SafeMode, Unsaved-Marker, Edge-Cases) und Read-Smoke (IntPtr.Zero → kein UIA-Text, kein Crash). e2e-Tests gegen echtes Office entfallen in der Sandbox (Martin 2026-07-04). |

### Tests

- 54 neue Tests in `tests/AiRecall.Core.Tests/AppReaders/{Word,Excel,PowerPoint}AppReaderTests.cs`.
- Test-Count gesamt: 243 / 243 grün (vorher 189 / 189).

### Verworfen

- **COM-Interop für Word/Excel/PowerPoint** (`Microsoft.Office.Interop.*`): würde Office-Installation
  voraussetzen, ist auf vielen Maschinen nicht vorhanden, und die Bindung an spezifische
  Office-Versionen macht die Pflege teuer. UIA liefert einen akzeptablen Ausschnitt
  ohne diese Abhängigkeit.
- **Folien-Nr / Sheet-Name / Notizen via UIA**: in Tests nicht zuverlässig abrufbar,
  wäre nur über COM oder Office-Add-Ins sinnvoll. Explizit als „nicht implementiert"
  im Output-Markdown dokumentiert.

---

## 2026-07-04 — Office COM-Erweiterung + PDF-Viewer (Spec 0004 Iter. 2)

Martin 2026-07-04: Office-Reader um COM-Komponenten erweitern (echter Pfad + Inhalt),
zusätzlich neue App-Familie PDF-Viewer.

| # | Thema | Entscheidung | Begründung |
|---|---|---|---|
| 1 | COM-Strategie | **Late binding** via ProgID + `Type.InvokeMember` — keine PIAs / NuGet-Pakete | Office ist nicht auf jeder Maschine installiert. Late binding funktioniert, sobald die COM-Server (Office selbst) vorhanden sind. Keine Build-Zeit-Abhängigkeit von Office-Versionen. |
| 2 | `GetActiveObject` | **P/Invoke auf `oleaut32.dll!GetActiveObject`** statt `Marshal.GetActiveObject` | `Marshal.GetActiveObject` ist in .NET 8 SDK 8.0.422 nicht (mehr) direkt verfügbar. P/Invoke ist der robuste Weg, der in allen SDK-Versionen funktioniert. |
| 3 | Inhalt-Speicherung | **In bestehende `*.content.md` integriert** (Sektion `## Document content (via COM)`) — **kein** separates File | Capture hat schon Screenshot + Pfad zur Quelldatei → alles in einer Datei verlinkt. Separate MD-Datei wäre Duplikation ohne Mehrwert. Martin-Default. |
| 4 | `FullPath` im Output | **`filePath` im Extra-Dict** → CaptureWriter rendert es als YAML-Frontmatter-Feld | Bestehende Mechanik: `AppReaderResult.Extra` → `CaptureWriter.RenderContentMarkdown` schreibt jedes KV-Pair als YAML-Zeile. Kein neuer Code noetig. |
| 5 | Excel-Inhalt | **UsedRange als Markdown-Tabelle** (object[,] → Pipe-Syntax) | COM `UsedRange.Value` ist 2D-Array; native Markdown-Tabelle ist die natürlichste Darstellung. Cell-Pipe-Escaping + Length-Truncate bei >60 Zeichen pro Zelle. |
| 6 | PowerPoint-Inhalt | **Slides als `### Slide N`-Liste** mit Text-Frames | COM hat keine „Inhalt"-Property für eine ganze Präsentation; pro Slide die Shapes durchlaufen und `HasTextFrame` + `TextRange.Text` sammeln. SmartArt/Tabellen fehlen in Iter. 2. |
| 7 | Word-Inhalt | **Range.Text (Plain-Text in Code-Block)** | Einfachster Word-Output; Markdown-Konvertierung in Iter. 3 via OOXML oder ReverseMarkdown-Word-Adapter. |
| 8 | COM-Fallback | **Bei jedem COM-Fehler (kein Office, andere Instanz, Exception) → null → Reader fällt auf UIA+Title zurück** | Nie crashen. UIA ist eh schon da; Office ist nur ein Bonus. Reader-Logik: erst COM versuchen, wenn null → Fallback. |
| 9 | COM-Prozess-Disambiguierung | **Nur erste Instanz** (für Iter. 2) | 99% der Fälle ist nur eine Office-Instanz offen. Pro-Prozess-Filterung (PID-Match) ist Iter. 3, YAGNI jetzt. |
| 10 | PDF-Viewer-DLL | **Neue DLL `AiRecall.AppReader.Pdf`** mit `PdfViewerAppReader` | Eine DLL pro App-Familie (analog zu Documents). Process-Liste konfigurierbar (`appReader.pdf.processes`), Default: Adobe/Sumatra/Foxit/PDFXChange/Edge/Chrome. |
| 11 | PDF-Viewer-Inhalt (Iter. 1) | **Nur Title-Parsing** (Filename + voller Pfad + Page-Nr) | PDF-Inhalt-Extraktion braucht eine PDF-Parser-Library (`PdfPig` ist Kandidat). In Iter. 2 mit NuGet-Package. Iter. 1 liefert Pfad-Hinweis im MD, damit der Capture zuordenbar ist. |
| 12 | PDF-Page-Info | **SumatraPDF-Style: `"file.pdf - Page N of M - SumatraPDF"`** | Andere PDF-Viewer zeigen Page-Nr nicht im Titel. Parsing ist robust: Page-Sep erst, dann Pfad-/Filename-Extraktion. |
| 13 | Office-COM-Tests | **`[Trait("Integration", "Office")]`** für COM-spezifische Tests | Sandbox hat kein Office → e2e-Smoke-Tests entfallen. Tests prüfen Struktur (Extra-Dict hat `source: com`, `filePath` gesetzt), laufen aber nur bei installiertem Office. |

### Tests

- 17 neue Unit-Tests in `PdfViewerAppReaderTests`.
- 3 neue Office-COM-Integration-Tests in Word/Excel/PowerPointAppReaderTests.
- Bestehende Office-Tests an COM-Pfad angepasst (Filename statt Markdown-Prefix).
- Test-Count gesamt: **263 / 263 grün** (vorher 243).

### Verworfen

- **Microsoft.Office.Interop.* NuGet-Pakete (PIAs)**: wuerde Office-Versionen ans Build-System binden. Late binding ist version-agnostisch.
- **Separate `*.document.md` pro Capture**: Martin bestaetigt Default „integriert". Falls er doch separate Datei will, ist die Aenderung klein (`CaptureWriter.WriteContent` + Reader rueckgabe).
- **PDF-Inhalt in Iter. 1**: wuerde NuGet-Abhaengigkeit (PdfPig ~5 MB) bedeuten und neue Fehlerquellen. YAGNI; iter. 2 mit NuGet-Evaluierung.
- **COM-Pro-Prozess-Disambiguierung (PID-Match)**: zu 99% nicht noetig; iter. 3 wenn Martin es wirklich braucht.

---

## 2026-07-04 — Office COM Iter. 3: Pro-Instanz-Filename-Match

Martin 2026-07-04 (Folgeanforderung nach Iter. 2): „Ermittle mit COM auch den Pfad zur aktuellen Datei / active document location." Hintergrund: `GetActiveObject("Word.Application")` liefert immer die erste laufende Instanz. Bei mehreren parallelen Office-Instanzen (z. B. zwei Word-Fenster mit unterschiedlichen Dokumenten) liefert COM sonst den falschen Pfad.

Loesung statt Pro-Prozess-COM-Bindung (zu komplex, kein direkter API-Weg in Windows): Filename-Match.

| # | Thema | Entscheidung | Begründung |
|---|---|---|---|
| 1 | Disambiguierung | **Filename-Match statt Pro-Prozess-COM-Bindung** | Es gibt in Windows keinen einfachen Weg, COM an einen bestimmten Prozess zu binden (außer über ROT mit Item-Moniker oder `AccessibleObjectFromWindow`). Filename-Match ist eine pragmatische 95%-Loesung: bei mehreren parallelen Instanzen mit unterschiedlichen Filenames passt der Match nicht → Fallback. |
| 2 | Match-Logik | **`MatchesExpectedFilename(string? fullPath, string? expectedFilename)`** als internal static Helper in `OfficeComInterop` | Eigenstaendig unit-testbar ohne COM. Wird in `TryGet` nach dem Lesen von `FullName` aufgerufen. Bei Mismatch → `null` → Reader faellt auf UIA+Title. |
| 3 | expectedFilename-Quelle | **`ParseTitle(window.Title)` vor COM-Lookup** | Filename aus Window-Titel parsen, an COM durchreichen. Wenn `ParseTitle` "(untitled)" oder den Default-Untitled-Marker (`Document1`/`Book1`/`Presentation1`) liefert, wird `expectedFilename = null` gesetzt (kein Match erzwungen) — sonst wuerde COM bei echtem Untitled-Doc immer mismatchen. |
| 4 | IsLikelyARealFilename | **Heuristik pro Reader** (private static) | Pro App unterschiedliche Untitled-Marker (`Document1` / `Book1` / `Presentation1`). Helper verhindert, dass diese als expectedFilename durchgereicht werden. Drei Zeilen pro Reader; DRY waere overkill. |
| 5 | Fallback-Strategie | **Bei Mismatch → null → Reader-Code faellt auf UIA+Title** | Wichtig: kein falscher Pfad in `content.md`. Im Gegensatz zu Iter. 2 (COM-Fehler) liefert Mismatch trotzdem null; Leser sieht keinen Unterschied. |
| 6 | Tests | **8 neue Unit-Tests** in `OfficeComInteropFilenameMatchTests` | null/empty expected (immer true), match, case-insensitive, mismatch, empty/null fullPath, unsaved-Doc-Sonderfall. |

### Tests

- 8 neue Unit-Tests.
- Test-Count gesamt: **271 / 271 grün** (vorher 263).

### Verworfen

- **PID-basierte COM-Bindung** (z. B. via `AccessibleObjectFromWindow` + `IUnknown::QueryInterface`): zu komplex fuer den Use-Case. Filename-Match deckt 95% der Realfaelle ab (mehrere Office-Instanzen mit identischem Filename sind ein Edge-Case, der in der Praxis selten vorkommt).
- **WindowClass-Match** (z. B. `_WwG` fuer Word): process-name + filename-match reicht aus. WindowClass ist sprachversions-abhaengig.

### Verworfen

- **`EVENT_OBJECT_SELECTION` als Trigger-Quelle**: würde bei Caret-Wechsel
  in Textfeldern jeden Tastendruck als Capture-Event interpretieren.
  Zu viel Rauschen, dedup würde die meisten schlucken.
- **Trigger-Mode „events" mit Heartbeat an**: unnötig, da WinEventHook
  in der Praxis zuverlässig ist. Heartbeat nur als explizit aktivierter
  Fallback oder im `both`-Mode.
- **`getAppContext` mit Modal-Kontext** (Option (b) der Diskussion): würde
  den App-Reader aufrufen, was bei modalen Dialogen oft leer/irrelevant
  ist. Frontmatter-Only (Option a) ist sauberer.

---

## 2026-07-04 — Trigger-Pipeline: WinEventHook statt Polling

`recall record` (Spec 0005) löst das ursprüngliche Polling auf
`GetForegroundWindow` (MVP1-Scope TR-1..6) durch eine event-basierte
Architektur ab.

| Aspekt | Entscheidung | Begründung |
|---|---|---|
| Primäre Trigger-Quelle | **`SetWinEventHook` out-of-context** (systemweit, ohne DLL-Injection) | Events kommen asynchron, granular (FOREGROUND/FOCUS/NAMECHANGE/VALUECHANGE/SCROLL/MENUPOPUP), keine CPU-Last durch Polling, keine Latenz zwischen Ereignis und Capture. |
| Sekundäre Trigger-Quelle | **Heartbeat-Polling** (`trigger.heartbeatIntervalSeconds`, Default 30 s) | Fallback für verschluckte Events (Sleep/Resume, hohe Systemlast). Niedrige Frequenz, reine Foreground-Erkennung, kein Inhalts-Polling. |
| `WH_SHELL` / `WH_CBT`-Hooks | **Verworfen** | Würden DLL-Injection in jeden anderen Prozess erfordern. Zu invasiv (Admin-Rechte, AV-Warnungen, Stabilitätsrisiko). |
| UIA-Event-Handler (`IUIAutomation.AddAutomationEventHandler`) | **Verworfen als primäre Quelle** | App-Coverage dünner als WinEventHook; COM-Interop in C# aufwendig. Kann später als Ergänzung dienen, nicht als Ersatz. |
| Throttle statt Debounce | **`trigger.throttleMs` (Default 500 ms)** — max 1 Capture pro HWND pro Zeitfenster | Klassisches Throttle-Pattern. Debounce („warte bis Ruhe") liefert bei aktivem Scrollen zu lange Pausen. |
| Per-App-Throttle | **`trigger.throttlePerAppSeconds` (Default 2 s)** | Zusätzliche Bremse: verhindert Capture-Bursts bei schneller Tab-Navigation in derselben App. |
| Hash-Dedup | **SHA-256 über `processName + contentText + title`, gespeichert pro HWND in `Dictionary<IntPtr, string>`** | Verhindert redundante Captures bei reinem Titel-Wechsel ohne Inhalts-Wechsel. Nicht über Screenshot-Hash (sonst flackern minimale Pixeländerungen). Verschiedene Fenster derselben App deduplizieren unabhängig voneinander (Diskussion 2026-07-04, Punkt 4). |
| Always-on-Top-Filter | **`WS_EX_TOPMOST` ist kein Ausschlusskriterium** | Viele legitime Apps sind AOT (Sticky Notes, Calculator, Chat). Filtern würde zu Lücken führen. |
| Modale Dialoge | **Eigenes Capture + Parent-Context als Frontmatter** | Bei Word „Speichern unter" o. ä. nur das Vordergrund-Fenster lesen, aber `parentHwnd`/`parentTitle`/`parentProcess` ins Frontmatter. Diskussion 2026-07-04, Option (a). |
| Tooltip/Notification-Filter | **Class-Blacklist** (`trigger.blacklist.windowClasses`) | Default: `tooltips_class32`, `NotifyIconOverflowWindow`. User-erweiterbar via Config. |
| Self-Capture-Filter | **PID-Vergleich** (`pid == Process.GetCurrentProcess().Id`) | Verhindert Aufzeichnung des eigenen Capture-/Konfig-Dialogs. |
| Child-HWND-Normalisierung | **`GetAncestor(hwnd, GA_ROOT)`** vor Throttle-Check | Button-Klick in Word triggert `EVENT_OBJECT_FOCUS` auf Button-HWND; normalisiert wird auf das Word-Fenster. |
| Outlook-Polling | **Bleibt in Spec 0004** unter `appReader.outlook.*` (`pollIntervalSeconds`, Default 60 s) | Mail-Stream ist inhärent polling-basiert (kein OS-Event für „neue Mail"). Konvention: app-spezifische Polling-Configs liegen unter `appReader.<reader>.*`, **nicht** unter `trigger.*` (Diskussion 2026-07-04, Punkt 3). |

### Auswirkungen

- Neue Spec: `specs/0005-trigger-pipeline.md` mit TR-1..9 (TR-1..6 aus
  MVP1-Scope bleiben gültig, +TR-7..9 für Tooltips/Modal/Child-HWND).
- Neue Top-Level-Config-Sektion `trigger.*` in `default-config.json`
  und `%APPDATA%/AiRecall/config.json`.
- Neue Komponente: `TriggerService` (IHostedService) in
  `AiRecall.ScreenCapture/Trigger/`.
- `EventHookThread` + `WorkerThread` + `Channel<TriggerEvent>` als
  Pipeline-Backbone.
- Capture-Writer-Frontmatter wird erweitert um optionale
  `parentHwnd`/`parentTitle`/`parentProcess` (bereits in `AppReaderResult.Extra`
  andeutungsweise vorhanden — wird konkretisiert).
- `recall record` CLI-Subcommand startet den Service und blockiert den
  Hauptthread bis Ctrl+C / SIGTERM.

### Verworfen

- **Stures Polling auf `GetForegroundWindow` (z. B. 1-Hz)**: Latenz,
  CPU-Last, verpasste kurze Events. Nur als Heartbeat-Fallback behalten.
- **Polling + OCR jedes Frames** (Screenpipe-artig): viel zu viel
  Rauschen + CPU/IO-Last für unseren Anwendungsfall (Recall-ähnliche
  semantische Erfassung, nicht Full-Framerate).
- **CDP/WebDriver-Trigger** (z. B. via Chrome Extension): Out of scope,
  Browser-spezifisch, würde Architektur in den Browser ziehen.
- **WPF-/Forms-spezifische Application-Idle-Events**: nur prozess-lokal,
  nicht systemweit.
- **Trigger-Pipeline als separate Library/DLL**: Anfangs in
  `AiRecall.ScreenCapture` geplant, dann doch in eigene
  `AiRecall.Trigger.dll` extrahiert (Martin 2026-07-04, Commit 11dea77),
  weil die MVP2-Tray-Icon-EXE denselben Code nutzen soll und ScreenCapture
  nicht braucht. Ref-Kette: Core → AppReader.Base → ScreenCapture →
  Trigger → Cli (zyklusfrei).

## 2026-07-03 — MVP1 Tech-Defaults

Offene Punkte aus `specs/0002-mvp1-scope.md` durch Martin bestätigt
(oder Default gesetzt):

| # | Thema | Entscheidung | Begründung |
|---|---|---|---|
| 1 | OCR-Engine | **Tesseract** (lokal, mehrsprachig) | Martin: "Build in OCR". Multi-OS-tauglich, kein Microsoft-Cloud-Zwang, MIT-kompatibel. |
| 2 | CLI-Library | **Manueller Switch** (wie vorhanden) | Nur 5 Commands geplant; System.CommandLine/Spectre wären unnötiger Ballast. Switch-Pattern in `Program.cs` ist < 30 Zeilen. |
| 3 | Logging | **Serilog 3.1.1** + Console + File | Strukturiertes Logging, tägliche Rolling-Files, Standard im .NET-ökosystem. |
| 4 | Tests | **xUnit** (bereits eingerichtet) | Bereits im Skeleton, gut für parallele Tests + VS-Integration. |
| 5 | Ignore-Liste | **Blacklist-Ansatz** mit kleinen Seed-Patterns | Default-Config seeded `1Password`, `KeePass`, `Bitwarden`, ein paar Title-Patterns (`Sign in`/`Anmelden`/`Passwort`/`Fingerprint`) und zwei URL-Patterns (`banking`, `accounts.google.com`). User kann via `%APPDATA%/AiRecall/config.json` erweitern. |

### Auswirkungen

- **Tesseract 5.2.0** als NuGet-Paket in `AiRecall.ScreenCapture`. Tessdata-Dateien sind nicht im Repo, Anleitung in `README.md` und `specs/0003-active-window.md`.
- **SerilogSetup** liegt in `AiRecall.Cli/Logging/` (nicht in Core), damit Core keine Sink-Deps braucht.
- **Default-Config** wird als `default-config.json` ins Output kopiert (`<None CopyToOutputDirectory="PreserveNewest">` im csproj).
- **System.Drawing.Common** braucht `UseWindowsForms=true` in `AiRecall.ScreenCapture.csproj` (für `Bitmap`/`Graphics`).

### Verworfen

- Windows.Media.Ocr — eingeschränkte Sprachunterstützung auf älteren Windows-Versionen, weniger portabel.
- System.CommandLine — Beta, größerer Refactor für 5 Commands unnötig.
- Spectre.Console.Cli — nett, aber ebenfalls Overhead ohne klaren Gewinn bei aktuellem Scope.
- Microsoft.Extensions.Logging — weniger mächtig als Serilog für strukturierte Capture-Pipeline.
- NUnit / MSTest — kein Mehrwert vs. xUnit bei aktuellem Bedarf.

## 2026-07-02 — Initial-Setup-Entscheidungen (aus Spec 0002)

- Lizenz: MIT
- Zielgruppe: Personal (nur Martin)
- Plattform: Windows only (MVP1)
- Solution-Struktur: Hybrid (zentrale `ScreenCapture`-DLL + `AppReader.Base` + separate App-Reader-DLLs)
- Trigger: Window-Activate + Scroll + Click mit Throttle + Dedup (Polling-basiert)
- Persistenz: Files only (MD + PNG, kein SQLite in MVP1)
- Outlook-Variante: Classic (MAPI/COM)
- GitHub-Repo: `schirkan/ai-recall` (public)

## 2026-07-03 — Browser-Reader: CDP als opt-in Pfad

Browser-Reader Iter. 3 führt Chrome DevTools Protocol (CDP) als optionalen
zweiten Pfad ein, zusätzlich zur bestehenden UIA-Strategie.

| Aspekt | Entscheidung | Begründung |
|---|---|---|
| Master-Switch | `appReader.browser.cdp.enabled = false` (Default) | Browser muss mit `--remote-debugging-port` gestartet werden — das ist ein manueller Schritt, den wir per Default nicht erzwingen wollen. UIA-Pfad funktioniert ohne weitere Konfiguration und bleibt Default. |
| Endpoint | `http://localhost:9222` (Default, konfigurierbar) | Standard-Port für Chrome DevTools. Konfigurierbar für Remote-Browser oder Custom-Ports. |
| Timeout | `1500 ms` (Default, konfigurierbar) | Ausreichend für lokales Loopback bei großen Pages; Tests laufen mit 100–200 ms ohne Hänger. |
| HTML → MD | `ReverseMarkdown 3.13.0` (NuGet) | Reichhaltigere Strukturen als UIA-Plain-Text; etabliertes Projekt, MIT-Lizenz. |
| Strategie-Reihenfolge | CDP-Versuch zuerst, UIA-Fallback | Bei aktiviertem CDP liefert ein Roundtrip URL + strukturiertes Markdown; ohne aktiven CDP-Server fällt es ohne Verzögerung auf UIA zurück. |
| Firefox-Support | Bleibt vorerst out of scope | CDP-Pfad ist über Edge/Chrome erschlossen; Firefox-CDP kann später nachgezogen werden, ohne Architekturänderung. |

### Auswirkungen

- `ChromeDevToolsProtocolClient` bleibt `internal static` in `AiRecall.AppReader.Browser` (kein Public-API-Bruch).
- `BrowserConfig.Cdp` ist neu in `AppConfig.cs`; `BrowserAppReader` greift darauf zu und reicht es durch.
- Default-Config (`default-config.json`) hat den Block `appReader.browser.cdp` mit `enabled: false`.
- Spec 0004 wurde entsprechend angepasst: Browser-Strategie-Sektion, Configuration-Sektion, Out-of-Scope-Hinweis zu Firefox relativiert.

### Verworfen

- **CDP hart aktivieren als Default:** Würde bei Usern ohne explizit gestarteten Debugging-Port sofort scheitern oder den Browser-Prozess suchen müssen — UX-Risiko zu hoch für MVP1.
- **Permanente CDP-Instanz pro Capture:** Worker-Lifecycle unnötig; gelegentlicher Roundtrip reicht.
- **CDP in separater DLL (`AiRecall.AppReader.Cdp`):** Overhead für eine einzige Klasse mit klarer Zuordnung zum Browser-Reader; bleibt in `Browser`-DLL.

## 2026-07-03 — Browser-Reader: ReverseMarkdown-Konfiguration 1:1 über JSON

Alle öffentlichen Properties von `ReverseMarkdown.Config` (v3.13) werden über
`appReader.browser.markdown` als JSON konfigurierbar gemacht. Damit lässt
sich das HTML→Markdown-Verhalten des Browser-Readers zur Laufzeit anpassen,
ohne Code-änderung.

| Aspekt | Entscheidung | Begründung |
|---|---|---|
| Konfigurations-Sektion | `appReader.browser.markdown` (Geschwister zu `cdp`) | Unabhängig vom CDP-Gate; spätere HTML-Quellen (z. B. Reader-Mock oder direkte Page-Quellen) sollen dieselbe Konfiguration nutzen können. |
| POCO-Design | Alle Felder als Nullable (`bool?`, `string?`, `List<string>?`) | Nicht gesetzte Felder werden **nicht** in `ReverseMarkdown.Config` geschrieben → Library-Defaults bleiben unangetastet. |
| Enums | Als JSON-Strings (case-insensitive, `Enum.TryParse`) | JSON hat keine native Enum-Repräsentation; Strings sind lesbar und ändern sich nicht, wenn die Library neue Enum-Werte einführt (alter Wert bleibt Default). |
| `ListBulletChar` (char) | Als String in JSON, nur erstes Zeichen übernommen | JSON hat keinen einzelnen `char`; Strings mit beliebigem Inhalt sind robuster (z. B. `\"->\"` → `'-'`). |
| Converter-Lifecycle | **Per-Call-Build** statt statisches Singleton | Jeder `Read` baut einen frischen `ReverseMarkdown.Converter` mit aktueller Config — vermeidet stale-state, wenn der User die Config zwischen Calls ändert (z. B. via Config-Reload). |
| Defaults in `default-config.json` | `unknownTags: \"PassThrough\"`, `githubFlavored: false`, `removeComments: true`, `smartHrefHandling: false`, `tableWithoutHeaderRowHandling: \"Default\"`, `listBulletChar: \"*\"`, `defaultCodeBlockLanguage: \"\"`, `whitelistUriSchemes: [http, https, ftp, ftps, mailto, tel]` | Setzt sinnvolle Defaults, die von der Library abweichen, wo wir das Verhalten explizit anders wollen (z. B. `listBulletChar: \"*\"` statt Library-Default `-`; `removeComments: true` weil `StripNoise` das sowieso schon macht). |

### Auswirkungen

- `AiRecall.Core/Configuration/AppConfig.cs` bekommt neue Klasse `MarkdownSettings`.
- `BrowserAppReader.cs` verliert den statischen `ReverseMarkdown.Converter`; neue `BuildConverter(MarkdownSettings?)`-Methode baut frischen Converter.
- `ConvertHtmlToMarkdown(html, maxChars, settings)` reicht die Settings durch.
- 11 neue Unit-Tests in `BrowserAppReaderTests` decken Default-Erhalt, alle Felder, Case-Insensitivity für Enums, ungültige Enum-Strings und End-to-End-Konvertierung ab.
- Spec 0004 wurde um den `markdown`-Block im Konfigurations-Abschnitt und ein neues Akzeptanzkriterium erweitert.

### Verworfen

- **Caching des Converters pro Settings-Hash:** Spart Mikrosekunden pro Call; lohnt den Komplexitäts-Aufwand (Hash-Berechnung, Dictionary-Lookup) bei unserer Call-Frequenz nicht. Read ist ohnehin O(HTML-Größe).
- **Converter-Konfiguration über Reflection auf private Felder:** Würde private Implementierungs-Details der Library koppeln; die offizielle `Config`-Property reicht.
- **Automatische Schema-Generierung aus der DLL:** Reflection auf die `ReverseMarkdown.dll` haben wir einmalig zur Verifikation gemacht (siehe `temp/reversemd-inspect/`); für die laufende Konfiguration ist die statische POCO-Definition klarer und typgeprüft.

---

## 2026-07-04 — Async Document Conversion Pipeline (Spec 0007, v1.0 abgeschlossen)

App-Reader entkoppelt von MD-Generierung. Reader liefern nur strukturierte
Metadaten (Title, FilePath, ggf. UIA-Content), zentrale async
Conversion-Pipeline assemblet daraus das finale `*.conversion.md`.
Commits `3a98e04` … `84afab7`. Test-Count 331/331 grün (vorher 271).

| # | Thema | Entscheidung | Begründung |
|---|---|---|---|
| 1 | Pandoc-Integration | **Verworfen** (Martin 2026-07-04 19:12) | „Pandoc ist Performance-mäßig raus." Format-Coverage < Performance. Konverter bleiben in-process (.NET-Libraries). Edge-Cases (odt, latex, epub, rtf, docbook) liefern `null` + Log statt Krücke über externen Process-Spawn. |
| 2 | Format-Mapping | **DocumentFormat.OpenXml + UglyToad.PdfPig + ReverseMarkdown** | OpenXml (MIT, MS, 700M+ Downloads) für docx/xlsx/pptx; PdfPig (Apache 2.0, 21M+ Downloads) für PDF; ReverseMarkdown (MIT, vorhanden) für HTML. ClosedXML (nur xlsx), NPOI (auch alte binäre Formate), iText7 (AGPL-Show-Stopper), PdfSharp (Fokus write) explizit verworfen. |
| 3 | Channel-Topologie | **In-process `Channel<string>`** (Martin 2026-07-04 19:25) | Producer-Consumer-Queue im Code sichtbar, testbar, deterministisch, plattform-neutral, kein Win32-FileSystemWatcher. SingleReader/MultiWriter, unbounded. |
| 4 | OCR-Pipeline | **Async im ConversionWorker** (Martin 2026-07-04 19:25) | Tesseract (100–500 ms pro Bild) aus dem synchronen Capture-Pfad raus, läuft im `ConversionWorker`-Pool parallel zu DocumentConverter. `IOcrEngine`-Interface + `TesseractOcrEngineAdapter` (via `Task.Run` um sync `OcrEngine` gewrappt) + `NullOcrEngine` als Default. |
| 5 | Legacy-Handling | **Keins** (Martin 2026-07-04 20:01) | „Das Tool ist neu." `recall convert` ist reiner **Recovery-Subcommand** für gecrashte Sessions, kein `--include-legacy`-Flag, kein Migrations-Pfad für alte Captures. |
| 6 | TriggerService-Lifecycle | **ConversionWorker wird vom TriggerService besessen** | `ITriggerService`-Pattern: externe Injection möglich (`conversionWorker: null` → intern erzeugt). `Dispose` disposet nur owned Worker (Ownership-Flag `_ownsConversionWorker`). Tesseract-Init-Fehler (tessdata fehlt) → Fallback `NullOcrEngine` mit Warning, kein ctor-Crash. |
| 7 | App-Reader dünn | **`IsThinReader=true` + `ContentMarkdown=Platzhalter`** | Reader liefern nur Title/FilePath/ggf. UIA-Content. ConversionWorker assemblet `*.conversion.md` mit `## Document content`, `## OCR Content`, `## App Reader Content (UIA)`. Bei `IsThinReader=true` schreibt der TriggerWorker kein `*.content.md` (Race-Vermeidung mit ConversionWorker). |
| 8 | Output-Trennung | **`*.content.md` (App-Reader, sync) + `*.conversion.md` (ConversionWorker, async)** | Zwei verschiedene Files, kein Race. Schritt 7 nutzt diese Trennung: dünne Reader (Word/Excel/PowerPoint) schreiben kein `*.content.md`; nicht-dünne (Browser/Notepad) schreiben weiterhin sync. Konsolidierung wäre Schritt-7-Folge. |
| 9 | Frontmatter-Pattern | **`CaptureWriter.WritePending` initial + `UpdateConversionStatus` nachträglich** | Atomares Schreiben: erstes WritePending erzeugt PNG + MD mit `conversion: "pending"`, optional `filePath`/`uiaContent`. UpdateConversionStatus parst Frontmatter, updated/addiert `conversion`/`conversionError`/`conversionSteps`/`conversionTimestamp`/`converterUsed`. Body bleibt unverändert. |
| 10 | Frontmatter-Felder | `conversion` (pending/done/partial/failed), `conversionError` (semikolon-getrennt), `conversionTimestamp` (ISO-8601), `conversionSteps` (semikolon-getrennt, `key=value` Paare), `converterUsed` | `conversionSteps` strukturiert: `doc=ok,openxml-word;ocr=ok,tesseract;appreader=ok,uia`. Jeder Schritt hat eigenen Status. Diagnose via `recall status` und log. |
| 11 | OCR-Engine-Init-Fallback | `TriggerService` ctor: try/catch um `OcrEngine(config.Ocr)` → `NullOcrEngine` | tessdata-Default-Pfad fehlt in CI/Sandbox → ohne Fallback crasht der ctor. Mit Fallback: Warning-Log, ConversionWorker läuft ohne OCR. |
| 12 | ConversionWorker-Concurrency | **Channel-Reader-Task pro Worker, sequenziell pro Capture** | Pro Capture: erst DocumentConverter, dann OCR, dann Frontmatter-Update. Worker-Pool-Größe implizit durch Channel-Lese-Rate (1 Worker). Parallelität ggf. in Schritt-7-Folge (`batchSize`-Feld in `ConversionConfig` ist da, aber noch nicht ausgenutzt). |
| 13 | `recall convert` | **CLI-Subcommand, scannt Disk, enqueued ohne Blocking** | Recovery-Tool: `--path` (Default: Config-Root), `--max-wait` (Default 30s), `--config`. Wartet bis Channel leer ODER max-wait abgelaufen, gibt Counter aus, Exit-Code ≠ 0 bei `FailedCount > 0`. Ohne `--include-legacy`-Flag. |
| 14 | ProjectRef | `AiRecall.Trigger → AiRecall.Conversion` + `AiRecall.Cli → AiRecall.Conversion` | Zyklusfreie Ref-Kette: Core → AppReader.Base → ScreenCapture → Conversion → Trigger → Cli. Trigger und Cli nutzen Conversion als Library. |
| 15 | Tests | **+60 netto Tests** | DocumentConverter (37) + ConversionWorker (15) + OcrWorker (5) + TriggerService-Integration (6) + UIA-Content-Section (1) - 4 alte App-Reader-Tests ersetzt = +60. Test-Count gesamt 331/331 grün. |

### Tests

- 60 neue Tests (netto) in `tests/AiRecall.Core.Tests/Conversion/` und `tests/AiRecall.Core.Tests/Trigger/TriggerServiceConversionTests.cs`.
- Test-Count gesamt: **331 / 331 grün** (vorher 271).

### Verworfen

- **Pandoc-Integration** (Martin 2026-07-04 19:12): Performance wichtiger als Format-Coverage. Edge-Cases (odt, latex, epub, doc/xls/ppt alt) liefern `null` + Log mit `no-converter-for-<ext>`.
- **`recall convert --include-legacy`-Flag** (Martin 2026-07-04 20:01): Tool ist neu, keine Legacy-Captures zu konvertieren.
- **FileSystemWatcher** (Martin 2026-07-04 19:25): in-process `Channel<string>` reicht, keine Disk-Polling.
- **Sync OCR im TriggerWorker**: 100–500 ms pro Bild zu viel für den Capture-Loop; Tesseract läuft async im ConversionWorker.
- **Pid-basierte COM-Bindung für Office-Reader** (Spec 0004 Iter. 3): zu komplex; Filename-Match deckt 95% ab.
- **Eigener Office-OpenXML-Writer** zum Reverse-Konvertieren (MD → docx): nicht im Scope.
- **Worker als Windows-Service**: zu komplex; Background-Task im selben Prozess reicht.
- **Streaming-Konvertierung** (Pipe zu externem Tool ohne Temp-File): unnötig, da in-process.
- **NuGet-Pakete ClosedXML / NPOI / iText7 / PdfSharp** (Evaluiert 2026-07-04 19:30): alle haben spezifische Nachteile ggü. OpenXml + PdfPig + ReverseMarkdown.

### Auswirkungen

- Neue DLL: `AiRecall.Conversion` (ProjectRef → Core)
- Neue Config-Sektion: `conversion.*` (enabled, maxTextKB, batchSize, conversionTimeoutSeconds) — `ocr.*` bleibt separat am Root für Backward-Compat
- `CaptureWriter` API erweitert: `WritePending(...)` (initial) + `UpdateConversionStatus(...)` (Frontmatter-Update)
- `AppReaderResult.IsThinReader` neues Flag (default `false`)
- `TriggerWorker` ruft App-Reader **vor** `CaptureWriter.WritePending` auf, übergibt `filePath`/`uiaContent` aus dem Extra-Dict ins Frontmatter
- `TriggerService` besitzt den `ConversionWorker` als optionale Dependency
- `recall convert` neuer CLI-Subcommand
- Frontmatter-Schema erweitert: `conversion`, `conversionError`, `conversionTimestamp`, `conversionSteps`, `converterUsed`


---

## 2026-07-04 � MVP2 Tray-Icon-EXE (Spec 0006/0008/0009, v1.0 abgeschlossen)

Neue WinForms-EXE als alternative UI zur CLI. Martin-Direktive 22:18: Live Logviewer + Settings-Dialog. Architektur-Korrektur 22:29: in-process statt Subprozess.

Commits 5ab077a...875ae98. Test-Count 331 -> **416/416 gr�n** (+85).

| # | Thema | Entscheidung | Begr�ndung |
|---|---|---|---|
| 1 | Assembly-Struktur | **AiRecall.TrayApp.exe (WinForms, WinExe) + AiRecall.Trigger.dll als Library** | Tray-EXE referenziert Trigger-Library und instanziiert ITriggerService direkt. CLI und Tray-EXE teilen sich denselben Code via Trigger-Library. Zyklusfreie Ref-Kette: Core -> Trigger -> TrayApp (+ AppReader.* + Conversion). |
| 2 | Architektur (revidiert v0.2) | **In-process ITriggerService statt Subprozess** (Martin 22:29) | TrayApp ist ohnehin tot ohne Worker � Isolation bringt nichts. Cold-Start, MMF-IPC und Process-Supervision sind unn�tige Komplexit�t. ProcessSupervisor und MmfLogPipe aus v0.1 sind tot. |
| 3 | UI-Framework | **WinForms** (kein WPF, kein Avalonia) | WinForms-NotifyIcon ist out-of-the-box verf�gbar; WPF w�re Overhead f�r Notification-Area-Use-Case. Cross-Platform unn�tig (Windows-only per Spec 0001). |
| 4 | TriggerSupervisor | **In-process-Wrapper um ITriggerService** mit TriggerState (Stopped/Starting/Running/Stopping/Crashed) + StateChanged-Event + optionaler ServiceFactory f�r DI/Tests | Sauberer Lifecycle: Start -> Running, Stop -> Stopped, Restart = Stop + Dispose + Start mit neuer Config (Hot-Reload-Pattern). Crash-Pfad: ServiceFactory throws -> State=Crashed, CrashCount++, LastCrashAt gesetzt. |
| 5 | ServiceFactory-Pattern | **Func<AppConfig, ILogger, ITriggerService> als optionaler ctor-Parameter** | Tests k�nnen FakeTriggerService injecten, ohne WinEventHook/Heartbeat/Channel zu instantiieren. Production nutzt DefaultFactory = (c, l) => new TriggerService(c, l). |
| 6 | Hot-Reload | **TriggerSupervisor.Restart(newConfig) = Stop + Dispose alter Service + Start mit neuer Config** | Im Gegensatz zu Subprozess-Kill: kein Cold-Start (200-500 ms gespart), keine MMF-Reinit, kein Datenverlust. UI merkt kurz State=Stopping -> Starting -> Running. |
| 7 | In-Memory Log-Sink | **InMemoryLogSink (custom Serilog-Sink) mit Ringbuffer 10.000** | LogviewerWindow subscribed auf EventEmitted und appended live. Kein File-I/O, kein MMF. Bei Crash/Dispose: EventEmitted = null (detach subscribers). File-Tail als Fallback f�r History. |
| 8 | LogviewerWindow-UI | **WinForms DataGridView mit 4 Spalten (Time, Level, Logger, Message), Virtual-Mode aus** | 10.000 Zeilen sind OK f�r non-virtual DataGridView. Color-Coding nach Level (grau/blau/schwarz/orange/rot/fett-rot) via CellFormatting. Cross-thread-safe via BeginInvoke. |
| 9 | LogFilter | **Pure-Logic: MinLevel (LogEventLevel?) + SearchText (string?, case-insensitive)** | Au�erhalb der WinForms-UI, separat unit-testbar. Matches(LogEventEntry) kombiniert beide Filter. |
| 10 | LogviewerSession | **Bounded buffer (LinkedList + lock) subscribed auf InMemoryLogSink.EventEmitted** | Pure-Logic zwischen Sink und Window. Mehrere Sessions �ber denselben Sink m�glich (isolation per capacity). IsPaused ist UI-Hint, Session puffert weiter. |
| 11 | Settings-Dialog-UI | **TreeView links (Top-Level + Sub-Sektionen) + dynamisch generierte Form rechts (Label + Type-Editor pro Property)** | WinForms .NET 8 hat **kein PropertyGrid-Control**. Daher dynamische Form-Generierung mit Type-spezifischen Editoren aus PropertyEditorFactory: bool->CheckBox, int/long/string->TextBox, enum->ComboBox, List<string>->Comma-Separated-TextBox. |
| 12 | ConfigSchemaReflection | **Reflection auf AppConfig POCO-Typen, Single-Source-of-Truth** | Kein manuelles Schema-File (Drift-Risiko). GetTopLevelSections liefert 7 Top-Level + 5 Sub-Sections unter ppReader. FindByPath f�r hierarchische Suche. Filtert Read-Only + Sub-Config-Klassen aus Property-Liste aus. |
| 13 | ConfigSerializer | **JsonSerializer.Serialize (camelCase, indented) + SaveAtomic mit .bak-Backup + .tmp + File.Replace** | Atomic-Write-Pattern: temp file + rename, kein halb-geschriebenes File bei Crash. Backup der vorherigen Version vor jedem Save. |
| 14 | Hot-Reload via Restart | **SettingsDialog.Save -> TrayAppContext.ApplyConfig -> supervisor.Restart(newConfig)** | Ein-Trigger-Pfad: User klickt Save -> File geschrieben -> Service restartet mit neuer Config -> LogviewerWindow bleibt offen, zeigt neuen Service-Log. |
| 15 | Single-Instance | **Named-Mutex Local\AiRecall.TrayApp.SingleInstance** | Zweiter Start erkennt ersten via Mutex, bringt dessen Fenster in den Vordergrund. Bring-to-Front via FindWindow + SetForegroundWindow (Stub in Schritt 1, vollst�ndig in Schritt 4). |
| 16 | UserConfigLocator | **Statische Helper-Klasse in AiRecall.Trigger**, gibt ConfigLoader.DefaultUserConfigPath() zur�ck und LoadOrDefault(logger) mit Fallback auf 
ew AppConfig() | Trennt Config-Pfad-Logik von TrayApp. Testbar ohne WinForms. ConfigLoader bleibt statisch (DECISIONS.md Spec 0002 v0.1). |
| 17 | Refactor: Pure Logic in AiRecall.Trigger | **TrayIconState, UserConfigLocator, LogFilter, LogviewerSession, InMemoryLogSink, ConfigSchemaReflection, ConfigSerializer, PropertyEditorFactory** sind in AiRecall.Trigger (Library), nicht in AiRecall.TrayApp (WinExe) | Tests brauchen kein WinForms-Setup (UseWindowsForms=true im Test-csproj verursacht xunit-Aufl�sungsprobleme). Library-Code ist plattform-neutral und auch von CLI nutzbar. |
| 18 | LogSink-Aufl�sung | **Trick: Log.Logger global konfiguriert mit WriteTo.Sink(inMemoryLogSink)** | Serilog unterst�tzt custom Sinks via WriteTo.Sink(). InMemoryLogSink implementiert ILogEventSink mit Emit(LogEvent). Resultat: alle Log.Information(...) Calls landen sowohl in logs/trayapp-*.log als auch im In-Memory-Ringbuffer. |
| 19 | TriggerEvent-Subscription (Logviewer) | **NICHT implementiert** (Workaround: Logviewer liest Serilog-Events, nicht Trigger-Events) | TriggerEvent hat kein Serilog-Format; ein dedizierter Subscription-Pfad w�re eigene Architektur. Aktuelle L�sung: Logviewer liest Log-Output, der reichhaltiger ist (Level, Logger, Message, Exception, Timestamp). Trigger-Counter werden in TrayIcon-Tooltip angezeigt. |
| 20 | WinForms PropertyGrid | **NICHT verf�gbar in .NET 8** (war im alten .NET Framework verf�gbar) | Daher dynamische Form-Generierung. Alternative w�re WPF + WindowsFormsHost, aber Overhead. Eigene PropertyGrid-Implementation w�re mehrere Tausend Zeilen � YAGNI. |

### Tests

- +85 Tests (netto) in 	ests/AiRecall.Core.Tests/Trigger/:
  - TriggerSupervisorTests (13): State-Transitions, Restart mit neuem Service, Crash-Pfad, StateChanged-Event
  - InMemoryLogSinkTests (14): Ringbuffer, FIFO-Overflow, Thread-Safety, EventEmitted, SourceContext-Parse
  - TrayIconStateTests (8): State-zu-Menu-Item-Mapping, IconGlyph, InvalidState
  - UserConfigLocatorTests (3): Path-Resolution, LoadOrDefault, Logger-Callback
  - LogFilterTests (8): Level, Search, Case-Insensitivity, Combined, Clone
  - LogviewerSessionTests (12): Sink-Subscribe, Append, Capacity-Overflow, Clear, Filter, Pause, Multi-Session
  - ConfigSchemaReflectionTests (11): Top-Level, Sub-Sections, Path-Lookup, Property-Editing
  - ConfigSerializerTests (9): Round-Trip, Atomic-Write, Backup, Malformed-JSON
  - PropertyEditorFactoryTests (7): Type-Dispatch f�r bool/int/string/enum/List, ReadOnly
- Test-Count gesamt: **416/416 gr�n** (vorher 331).

### Verworfen

- **Subprozess-Spawn mit ProcessSupervisor + MmfLogPipe + MMF-IPC** (Spec 0006 v0.1): TrayApp ist ohnehin tot ohne Worker � Isolation bringt nichts. Martin-Korrektur 22:29.
- **TrayApp in WPF**: Overhead ohne Mehrwert f�r Notification-Area-Use-Case.
- **Avalonia/MauiUI**: Cross-Platform unn�tig.
- **MemoryMappedFile als IPC**: durch in-process-Architektur �berfl�ssig.
- **Named-Pipe f�r Log-Streaming**: durch in-process-Architektur �berfl�ssig.
- **WinForms PropertyGrid-Control**: nicht in .NET 8 verf�gbar � dynamische Form-Generierung stattdessen.
- **TriggerEvent-Subscription in LogviewerWindow**: redundant, da Logviewer bereits Serilog-Events liest.
- **Eigene PropertyGrid-Implementation**: mehrere Tausend Zeilen f�r ein Edit-Control, YAGNI.
- **<Using Include="Xunit" /> via Project-Reference** (vorheriger Fehler): xunit 2.6.6 wird mit explizitem <Using Include="Xunit" /> im Test-csproj aufgel�st (sonst scheitert Compile mit "Der Name 'Fact' wurde nicht gefunden").

### Auswirkungen

- Neue DLL: AiRecall.TrayApp (WinForms, WinExe, 
et8.0-windows)
- AiRecall.Trigger erweitert um TriggerSupervisor, InMemoryLogSink, TrayIconState, UserConfigLocator, LogFilter, LogviewerSession, ConfigSchemaReflection, ConfigSerializer, PropertyEditorFactory
- AiRecall.Cli unver�ndert (Standalone-Support f�r Scripts)
- AiRecall.Trigger ProjectRef in AiRecall.TrayApp
- AiRecall.TrayApp ProjectRef in Solution
- Test-csproj: <Using Include="Xunit" /> f�r implizites using Xunit;
- TrayAppContext orchestriert: InMemoryLogSink (Serilog) + TriggerSupervisor + TrayIconController + LogviewerSession
- Hot-Reload-Pattern: SettingsDialog Save -> ConfigSerializer.SaveAtomic -> TriggerSupervisor.Restart (kein Process-Kill)
- AiRecall.TrayApp.exe Start-Args: keine (Config aus %APPDATA%/AiRecall/config.json)





## 2026-07-05 — Outlook App-Reader (Spec 0004 Iter. 3)

Spec 0004 Iter. 3 ist mit Commits `f0dffb1` ... `42de0b9` abgeschlossen.
Test-Count 425 → **525/525 grün** (+100). Modul komplett neu in
`AiRecall.AppReader.Outlook` (6 Files, 0 Warnings, 0 Errors).

| # | Thema | Entscheidung | Begründung |
|---|---|---|---|
| 1 | DLL-Struktur | **Neue DLL `AiRecall.AppReader.Outlook.dll`** (analog zu Browser/Notepad/Documents) | Eine DLL pro App-Familie. Plugin-Discovery via AppReaderRegistry automatisch. |
| 2 | COM-Strategie | **Late binding** (ProgID `Outlook.Application` + P/Invoke `oleaut32!GetActiveObject`) | Wie `OfficeComInterop`. Keine PIAs, keine Office-Version-Bindung. Funktioniert sobald Outlook als COM-Server läuft. |
| 3 | Active-Object-Pattern | **P/Invoke statt `Marshal.GetActiveObject`** | `Marshal.GetActiveObject` ist in .NET 8 SDK 8.0.422 nicht (mehr) direkt verfügbar. P/Invoke funktioniert in allen SDK-Versionen. |
| 4 | Dual-Mode Reader | **`Read()` für aktives Fenster + `OnPoll()` für Background-Sweep** | `Read` für AR-2 (Inspector/Explorer-Selection); `OnPoll` für AR-3+AR-7 (Mail-Stream-Log). `SupportsBackgroundPolling = true` — der TriggerSupervisor ruft `OnPoll` zusätzlich zu `Read`. |
| 5 | `OnPoll`-Throttle | **Internes `_lastPollAt`-Feld** + Check gegen `cfg.PollIntervalSeconds` | Verhindert Sweep-Spam bei mehrfachen TriggerService-Calls. Default 60 s. Kein zusätzlicher Timer nötig. |
| 6 | EntryID-Dedup | **`OutlookEntryStore` mit `HashSet<string>` + atomic `File.Replace`** | Verhindert Re-Persistenz nach App-Restart. `IsSeen()` O(1), `MarkSeen()` atomar, `MarkSweepCompleted()` setzt Timestamp. State in `%APPDATA%/AiRecall/outlook-seen.json` (Plain-JSON, gitignored). |
| 7 | AutoRule-Heuristik | **4 Bedingungen, ≥2 Hits = suspect** | Pure-Function auf `MailSnapshot`-Record: (a) Marked-Read-Fast, (b) Junk/Newsletter-Folder, (c) NoReply-Sender-Regex, (d) Auto-Reply-Subject+Body. Suspect-Mails werden via `MarkSeen()` markiert (keine Re-Evaluation) aber **kein MD geschrieben**. |
| 8 | HTML→MD | **Custom-Konverter, kein ReverseMarkdown** | Outlook-HTML ist simpel genug für eigene Tag-Strip-Logik. Strippt `<style>`/`<script>`/Conditional-Comments/`<img>`; Links zu `[text](url)`; Block-Tags zu `\n\n`; `&nbsp;` → space; Whitespace-Normalisierung mit Block-Boundary-Tracker (`\n\n` bleibt erhalten). |
| 9 | Capture-Root-Default | **`%APPDATA%/AiRecall/capture`** als gemeinsamer Anchor | Gleicher Anchor wie `OutlookConfig.DefaultSeenStatePath()`. Überschreibbar via Konstruktor (`internal OutlookAppReader(store, logger, captureRoot)`). |
| 10 | Direction-Inferenz | **Folder-Name → in/out** | `Inbox` → `in`, `Sent Items` → `out`, Custom-Folder → `—`. Im Persistenz-Schema-Filename und im Frontmatter. |
| 11 | Persistenz-Schema | **`capture/<yyyy-MM-dd>/outlook-mail/<HHmmss>-<direction>-<entryIdShort>.md`** | Eine MD pro Mail. YAML-Frontmatter: `timestamp`, `kind=mail`, `direction`, `entryId`, `subject`, `from`, `folder`, `date`, `lastModificationTime`, `unread`, `hasAttachments`, `autoRuleSuspect`, `source=outlook-com`, `reader`, `readerVersion`. |
| 12 | Filename-Kollisionen | **`<HHmmss>-N`-Suffix** bei gleichem Timestamp | Outlook-EntryIDs sind eindeutig, aber im selben Sweep können zwei Mails gleichen `<HHmmss>` haben. Suffix `-1`, `-2`, ... verhindert Überschreiben. |
| 13 | Test-Injection | **`internal OutlookAppReader(store, logger, captureRoot)` + `[InternalsVisibleTo("AiRecall.Core.Tests")]`** | Tests können Dependencies isolieren, ohne `%APPDATA%` oder COM zu brauchen. Production-Pfad nutzt parameterlosen Konstruktor (AppReaderRegistry-Anforderung via `Activator.CreateInstance`). |
| 14 | Lazy-Init-Konsolidierung | **`EnsureInitialized(context)`-Helper** statt mehrerer `_initialized`-Flags | Klare Init-Logik, keine Doppel-Checks. Init für Store + CaptureRoot + Logger in einem Helper. -12 Zeilen Code ggü. initialer Version. |
| 15 | CS8605-Fix | **`(int)(InvokeMember(...) ?? 0)` defensiv** für COM Late-Binding Count-Properties | Casts auf `int` lösen CS8605 aus, weil `object?` nicht implizit kompatibel. Null-Coalesce-Default verhindert Warning. Pattern auch für `bool`: `(bool)(InvokeMember(...) ?? false)`. |
| 16 | Test-Trait-Marker | **`[Trait("Integration", "Outlook")]`** für COM-Tests | Sandbox hat kein Outlook → e2e-Smoke-Tests entfallen. Struktur-Tests laufen ohne COM. |

### Tests

- 100 neue Tests in 5 Files: `OutlookEntryStoreTests` (14), `OutlookAutoRuleDetectorTests` (20), `OutlookTitleParserTests` (16), `OutlookBodyToMarkdownTests` (23), `OutlookAppReaderTests` (27 = 18 Facts + 1 Theory mit 9 InlineData).
- Test-Count gesamt: **525/525 grün** (vorher 425).
- Outlook-Modul: **0 Warnings, 0 Errors**.

### Verworfen

- **Pandoc für Mail-Bodies**: zu groß, Mail-Bodies sind simpel genug für Custom-Konverter.
- **Separate MD pro Mail-Part (HTML/Plain)**: Mail ist atomar, ein MD mit YAML-Frontmatter reicht.
- **Auto-Disable bei Outlook-Neustart**: EntryID-Dedup fängt das ab — keine spezielle Recovery-Logik nötig.
- **Per-Folder Sweep-Parallelisierung**: `OutlookAppReader.OnPoll` ist single-threaded mit `lock (_gate)`. EntryID-Dedup macht Parallelisierung YAGNI.
- **Polling-Interval pro Folder**: ein globales `pollIntervalSeconds` reicht für MVP1.
- **Attachment-Speicherung**: `hasAttachments: true` im Frontmatter, keine Persistierung in Iter. 3 (Out of Scope für nächsten Cluster).
- **Outlook New (PIM-basiert)**: hat keine COM-Schnittstelle — bleibt Out of Scope (Spec 0004 Out-of-Scope-Liste).

### Auswirkungen

- Neue DLL: `AiRecall.AppReader.Outlook` (ProjectRef → `AiRecall.AppReader.Base`)
- Erweiterte Config-Sektion `appReader.outlook`: `folders`, `pollIntervalSeconds`, `ignoreAutoRuleMails`, `maxItemsPerSweep`, `bodyTruncateKB`, `htmlToMarkdown.{preserveLinks, preserveLineBreaks, stripImages}`
- Neuer Persistenz-Pfad: `%APPDATA%/AiRecall/outlook-seen.json` (Plain-JSON, gitignored, atomar via `File.Replace`)
- Neue Capture-Subdir: `capture/<yyyy-MM-dd>/outlook-mail/`
- Spec 0004 Iter. 3 vollständig dokumentiert (Motivation + Komponenten + Config + Schema + Tests + Einschränkungen)
- `AiRecall.Core/Configuration/AppConfig.cs` um `OutlookConfig` + `HtmlToMarkdownOptions` erweitert
- Commits: `f0dffb1` (Config+Spec), `ff1a0a2` (EntryStore), `587c1f4` (AutoRule), `57b52e5` (TitleParser+BodyToMD), `ef2469d`+`eae9817` (ComInterop), `fb0fe1a` (AppReader), `4ada756` (Doku), `42de0b9` (Refactor-Pass)


---

## 2026-07-06 â€” Roadmap-Reshuffle: MVP 3 (Audio Notes) + MVP 4 (Auto Wiki)

**Direktive Martin 2026-07-06 (kurz, Telegram):** â€žMvp 4 auto wiki Â· Mvp 3 audio notes.
Details folgen. Nur kurz dokumentieren."

**Entscheidung:** Roadmap-Nummerierung wird verschoben.

| #     | Alt (Spec 0001)                  | Neu                                                      |
|-------|----------------------------------|----------------------------------------------------------|
| MVP 1 | Screen Recorder + App Reader     | unverÃ¤ndert (abgeschlossen)                              |
| MVP 2 | Tray-Icon-EXE / Trigger          | unverÃ¤ndert (abgeschlossen)                              |
| MVP 3 | **Auto Knowledge Base / Wiki**   | **Audio Notes** (neu)                                    |
| MVP 4 | â€”                                | **Auto Knowledge Base / Wiki** (Wiki-Scope wandert von 3 nach 4) |

**BegrÃ¼ndung:** Die ursprÃ¼nglich unter MVP 3 zusammengefassten Scopes
(Audio-Capture + Wiki/Index) werden entzerrt, weil Audio-Capture
eigenstÃ¤ndige KomplexitÃ¤t mitbringt (NAudio/Whisper-Kette,
Kalender-Integration, Meeting-Start/Stop) und das Wiki darauf
aufbaut, aber separat releasebar ist.

**Konsequenz / Folge-Aktionen:**

- specs/0001-vision.md Roadmap-Sektion entsprechend reshufflet.
- PROJECT.md â€žOffene Punkte" bekommt einen kurzen MVP-3/4-Block.
- Spec-Detail-Specs (vermutlich  013-audio-notes.md,  014-auto-wiki.md)
  werden separat erstellt, sobald Martin die Details liefert.
- Hinweis: specs/0012-tessdata-first-run.md (Bug-Bash 2026-07-06) belegt
  bereits die 0012-Nummer â€” Audio-Notes/Wiki-Specs verschieben sich daher
  auf 0013+.

---
## 2026-07-06 � Bug-Bash TrayApp + Async Conversion (Spec 0006/0007/0008/0009)

Mehrere kleine Fixes aus einem Bug-Bash-Durchlauf am 2026-07-06. Commit-Reihe folgt.
Test-Count 650/650 gr�n (vorher 589).

| # | ID | Thema | Entscheidung | Begr�ndung |
|---|---|---|---|---|
| 1 | I-4 | Trigger-Lifecycle Dispose | (siehe Spec 0006) | Bereits in `TriggerSupervisor` korrekt; reviewed, kein Change. |
| 2 | I-9 | Logviewer Auto-Scroll Toggle | (siehe Spec 0008) | Bereits via `_programmaticScrolling`-Flag korrekt. |
| 3 | I-10 | Idempotent-Dispose | (siehe Spec 0006) | `_disposed`-Flag in `TrayAppContext` erg�nzt. |
| 4 | I-11 | Tray-Tastenk�rzel ohne Modifier | ShortcutKeys ben�tigt `Keys.Control | Keys.X` (nicht `Keys.X` allein) | `InvalidEnumArgumentException` beim ersten Start. Alle f�nf Men�-Items (Start/Stop/Logviewer/Settings/Quit) auf `Ctrl+...` umgestellt. |
| 5 | I-12 | Logviewer ScrollToEnd vor Shown | Range-Check `_grid.RowCount` + try/catch + `Shown`-Event | `FirstDisplayedScrollingRowIndex` l�sst sich nicht setzen, bevor die DataGridView-Layout-Pass gelaufen ist. Initiales Auto-Scroll aus dem Konstruktor in den `Shown`-Event-Handler verlegt. |
| 6 | I-13 | Logviewer Count-Refresh | 1 s `System.Windows.Forms.Timer` | Counter aktualisierte sich zwischen State-�berg�ngen nicht; `StateChanged` feuert nur bei Transition. Timer l�uft 1 Hz und liest `Service.CaptureCount` live. |
| 7 | I-14 | OCR tessdata-Pfad-Suche + User-Hinweis | Mehrere Suchpfade + Tray-Ballon | Default `tessdata` neben EXE ist auf Dev-Maschinen oft leer. Suchreihenfolge: konfigurierter Pfad (relativ/absolut) ? `%LOCALAPPDATA%\AiRecall\tessdata` ? `BaseDirectory\tessdata`. Wenn alles fehlt: One-Shot-Ballon mit Setup-Hinweis. Fallback bleibt `NullOcrEngine` (kein Crash). |
| 8 | I-15 | Tray-Counter-Refresh | (zusammen mit I-13) | s. o. |
| 9 | I-16 | SettingsDialog Layout | `FixedPanel=Panel1` + proportionaler Splitter + `Editor.Anchor=Top|Left|Right` | SplitterDistance war fixer Pixelwert (220), Editoren auf fixen 360 px. Jetzt: Panel1 bekommt nur so viel Platz wie n�tig (= 140 px), Editoren dehnen sich mit Panel2. `SplitContainer.SplitterMoved` und `_editorPanel.Resize` triggern `RelayoutEditors()`. |
| 10 | I-17 | In-Place-Content (ConversionWorker) | `CaptureWriter.WriteConversionContent` ersetzt `_(conversion pending)_` in der Capture-MD | Vorher: separates `*.conversion.md` + Original-MD behielt Pending-Platzhalter ? 2 Dateien pro Capture, Duplikation, kaputte UX. Jetzt: Content-Sections (Document / OCR / AppReader-UIA) landen direkt in der Capture-MD unter `## Content`. Kein `*.conversion.md` mehr. Zwei Nebenfixes: (a) `UpdateConversionStatus` las Datei via `StreamReader.ReadLine()` statt `Split('\n')` � vermied `\r\r\n`-Sequenzen, die im Frontmatter als Leerzeilen sichtbar waren; (b) Placeholder-Text auf `_(conversion pending)_` verk�rzt (vorher: `_(conversion pending � async via ConversionWorker)_`). Tests angepasst: 5 `*.conversion.md`-Assertions ? 5 in-place Assertions in `ConversionWorkerTests`/`ConversionWorkerOcrTests`. `TriggerServiceConversionTests.ConversionWorker_TesseractInitFails_FallsBackToNullOcrEngine` mit `Ocr.TessDataPath = <unique-temp>` deterministisch gemacht (vorher abh�ngig von lokalem tessdata-Ordner). |

### Tests

- Test-Count gesamt: **650 / 650 gr�n** (vorher 589).
- 5 Tests in `ConversionWorkerTests`/`ConversionWorkerOcrTests` umgeschrieben (in-place Assertions).
- 1 Test in `TriggerServiceConversionTests` deterministisch gemacht (TessDataPath auf unique temp).

### Verworfen

- **Auto-Download der tessdata beim ersten Start**: Offline-f�higkeit w�re unklar; Tray-Ballon + manueller Setup-Hinweis reicht. Kann sp�ter nachger�stet werden, wenn Distribution klar ist.
- **CounterChanged-Event am Supervisor**: schlanker als Timer-Poll, w�rde aber Engine-Side-�nderungen erfordern (Event in `TriggerWorker.CaptureCount++` emittieren). 1-Hz-Timer-Poll reicht f�r UX.
- **Pandoc-Re-Integration**: bleibt verworfen (Spec 0007 Decision 1).

### Auswirkungen

- `AiRecall.TrayApp/TrayIconController.cs`: Timer + ShowBalloon + RefreshStatus
- `AiRecall.TrayApp/TrayAppContext.cs`: MaybeWarnAboutMissingTessdata
- `AiRecall.TrayApp/Windows/LogviewerWindow.cs`: ScrollToEnd absichern, Shown-Event
- `AiRecall.TrayApp/Windows/SettingsDialog.cs`: SplitContainer proportional + RelayoutEditors
- `AiRecall.ScreenCapture/Text/OcrEngine.cs`: Mehrpfad-Suche f�r tessdata
- `AiRecall.Trigger/TriggerService.cs`: unver�ndert (Pfad-Suche wird in OcrEngine gehandhabt)
- `AiRecall.Core/Persistence/CaptureWriter.cs`: WriteConversionContent + StreamReader-Fix + Placeholder-Text
- `AiRecall.Conversion/ConversionWorker.cs`: kein separates `*.conversion.md` mehr, in-place
- Tests: 5 in-place Assertions, 1 deterministisch gemacht



---

## 2026-07-06 â€” Bug-Bash TrayApp + Trigger-Pipeline v2 (Teil 2 â€” Spec 0005/0006/0009)

Fortsetzung des Bug-Bash vom 2026-07-06, Commit d245dd2. 27 Issues aus
Real-Tests der TrayApp-Flows adressiert (I-18 bis I-25 in DECISIONS
dokumentiert; vorhergehender Block I-4 bis I-17 separat in 4e5e617).
Test-Count **650 â†’ 673 grÃ¼n** (+23).

| #   | ID    | Thema                                                       | Entscheidung                                                                                                                              | BegrÃ¼ndung                                                                                                                                  |
|-----|-------|-------------------------------------------------------------|-------------------------------------------------------------------------------------------------------------------------------------------|----------------------------------------------------------------------------------------------------------------------------------------------|
| 1   | I-23  | Periodic Capture                                            | TriggerService verdrahtet neuen PeriodicCaptureThread (3â€“10 s); TriggerKind.Periodic neu                                            | Video-Streams / Slideshows Ã¤ndern visuell, aber Hwnd + Titel bleiben gleich â†’ WinEventHook feuert nicht.                                    |
| 2   | I-24  | PollThread-Konsolidierung                                   | HeartbeatThread + PeriodicCaptureThread werden dÃ¼nne Wrapper um neuen internen PollThread                                          | Loop-Logik war 1:1 identisch (Cancellation-aware Sleep + GetForegroundWindow); Deduplication ~110 LoC.                                  |
| 3   | I-18  | ConfigSchemaReflection rekursiv                           | BuildSection/BuildTreeNode traversieren Property-BÃ¤ume rekursiv; IsExpandableConfigType prÃ¼ft Property-Typ                           | Vorher: nur Top-Level-Sektionen (rowser, pdf, ...); Sub-Sub-Configs (rowser.cdp, 	rigger.winEvents) fehlten komplett im Tree.     |
| 4   | I-21  | IsExpandableConfigType Property-Typ-Check                 | Typbasierte Erkennung statt Klassen-Attribut                                                                                              | Attribute-Drift war nicht garantiert; Property-Typ ist single-source-of-truth.                                                              |
| 5   | I-25  | Description-Attribute in AppConfig                        | 77 [Description]-Texte (deutsch) hinzugefÃ¼gt                                                                                            | Settings-Dialog soll pro Editor einen Hover-Text anzeigen; manuelles Doku-Drift vermeiden.                                                  |
| 6   | I-25 | Nullable-Editoren in PropertyEditorFactory                | ool?/int?/string?/Enum? mit â€žnull"-Sentinel-Wert                                                                                 | ool.Parse("null") warf im ersten Wurf; jetzt OrdinalIgnoreCase-Switch.                                                                 |
| 7   | I-16 | SettingsDialog proportionaler Splitter                       | SplitContainer.FixedPanel = Panel1, ~30 % TreeView, Editoren dehnen sich                                                                 | SplitterDistance war fixer Pixelwert (220 px); Editoren hingen auf 360 px.                                                                  |
| 8   | I-18 | SettingsDialog rekursive Section-Nodes                      | TreeView-Builder ruft sich rekursiv auf                                                                                                    | Vorher: 15+ flache Top-Level-Knoten; jetzt echte Baumstruktur (z. B. Browser â†’ CDP â†’ enabled).                                            |
| 9   | I-19 | SettingsDialog Layout                                       | StatusStrip + ButtonPanel in split.Panel2                                                                                                | Resize-Grip war unter den Buttons â†’ unbenutzbar.                                                                                            |
| 10  | I-20 | SettingsDialog Editor-Lookup                                | OrdinalIgnoreCase statt ool.Parse                                                                                                    | ool.Parse("null") warf bei Nullable<bool>; Switches auf String-Compare.                                                                  |
| 11  | I-25 | Description-Label                                           | 1-zeiliges Label unter jedem Editor                                                                                                       | User soll pro Feld Hover-Hilfe sehen ohne PropertyGrid-Tooltips.                                                                            |
| 12  | I-15 | 1-Hz-StatusRefresh-Timer                                    | TrayIconController hÃ¤lt Capture-Counter live                                                                                             | StateChanged feuert nur bei Transition; CaptureCount ist zwischen Events eingefroren.                                                   |
| 13  | I-UE | Quit-Icon via EmojiIconFactory                              | â€žx"-Emoji via COLR/CPAL statt embedded .ico                                                                                             | Single-Color-Fallback-Pfad fÃ¼r monochrome Tray-Icons reicht; reduziert Embedded-Ressourcen.                                                 |
| 14  | I-12 | Tessdata First-Run (Spec 0012)                              | Multi-Path-Suche (config-Pfad â†’ %LOCALAPPDATA%\AiRecall\tessdata â†’ BaseDirectory) + One-Shot-Ballon                                | 	essdata neben EXE auf Dev-Maschinen oft leer; Balloon-Hinweis besser als Silent-Crash.                                                   |
| 15  | I-22 | OcrEngine Tesseract-Pfad-Suche                            | 3-Pfad-Suche mit aussagekrÃ¤ftiger Error-Message                                                                                            | User sieht ohne Suche nicht, welcher Pfad eigentlich geprÃ¼ft wurde.                                                                         |

### Neue Spec / Datei

- specs/0012-tessdata-first-run.md (169 LoC): Auto-Download-Plan fÃ¼r 	essdata_fast-Repo,
  modaler TessdataFirstRunDialog, SHA-Check verworfen (Repo publiziert keine
  pro-File-Checksums), Apache-2.0-Hinweis, sequentieller Download mit 3Ã—Retry + Exponential-Backoff.
- 	ools/EmojiIconGen/Program.cs (144 LoC): Dev-Tool zum COLR/CPAL-Icon-Generieren.

### Tests

- Test-Count gesamt: **673 / 673 grÃ¼n** (vorher 650).
- +23 Tests in TessdataManagerTests (187 LoC), TreeDumpTest, PeriodicCaptureThreadTests,
  PropertyEditorFactoryTests, ConfigSchemaReflectionTests, TriggerEventTests,
  TriggerServiceConversionTests.
- Build clean (6 pre-existing nullable warnings in DocumentConverter.cs).

### Verworfen

- **MenuImageCache (screenshots/menus duplicate icon-stub)**: Dead-Code aus Vor-Spec-0006
  Phase, entfernt.
- **ppReader.maxContentKB Property**: durch Kommentar mit BegrÃ¼ndung ersetzt;
  pro-App-Reader-MaxContentKB ist weiterhin pro DLL vorhanden (Outlook, OneNote, Teams).
- **CounterChanged-Event am TriggerSupervisor**: wÃ¼rde Engine-Side-Ã„nderungen erfordern
  (Event in TriggerWorker.CaptureCount++ emittieren). 1-Hz-Timer-Poll ist schlanker.
- **Auto-Download tessdata silent (ohne User-Dialog)**: gegen Spec-0002-Prinzip
  â€žUser gibt Initial-Setup selbst". Spec 0012 sieht expliziten User-Confirm vor.

### Auswirkungen (Code-Dateien)

- AiRecall.Trigger/PollThread.cs (neu, 127 LoC) + HeartbeatThread.cs (Wrapper) + PeriodicCaptureThread.cs (neu, 41 LoC)
- AiRecall.Trigger/ConfigSchemaReflection.cs rekursiv (228 LoC Diff, BuildSection/BuildTreeNode)
- AiRecall.Trigger/TriggerEvent.cs (TriggerKind.Periodic neu)
- AiRecall.Trigger/TriggerService.cs (PeriodicCapture-Wiring in Start/Stop/Dispose/Logging)
- AiRecall.TrayApp/EmojiIconFactory.cs (neu, 150 LoC)
- AiRecall.TrayApp/Windows/WindowPlacement.cs (neu, 66 LoC, BottomRight-20px-Padding-Helper)
- AiRecall.TrayApp/Windows/SettingsDialog.cs (174 LoC Diff, rekursiv + Description-Labels)
- AiRecall.TrayApp/Windows/LogviewerWindow.cs (13 LoC Diff, SortDefaults + AutoScroll + OnShown)
- AiRecall.TrayApp/TrayIconController.cs (113 LoC Diff, RefreshTimer + Emoji-Icon-Pfad)
- AiRecall.TrayApp/TrayAppContext.cs (107 LoC Diff, Tessdata-Warning-Hook)
- AiRecall.TrayApp/Resources/Icons/*.ico (10 neue Icons: logs, quit, settings, start, status-{crashed,running,starting,stopped,stopping}, stop, tray-{idle,recording})
- AiRecall.Core/Configuration/AppConfig.cs (95 LoC Diff, 77 Description-Attribute + Ocr.AutoDownloadTessdata)
- AiRecall.Core/Persistence/CaptureWriter.cs (WriteConversionContent + StreamReader-Fix)
- AiRecall.Conversion/ConversionWorker.cs (in-place statt separates *.conversion.md)
- AiRecall.ScreenCapture/Text/OcrEngine.cs (3-Pfad-Suche)
- AiRecall.Cli/default-config.json (38 LoC Diff, neue Defaults)
- AiRecall.sln (32 LoC Diff, neue Projekte)
- 	esseract50.dll + leptonica-1.82.0.dll (Binary-Dependencies neu committed, Apache-2.0)
- 	ools/EmojiIconGen/EmojiIconGen.csproj + Program.cs (Dev-Tool, nicht in Production-Build)
- Tests: 187 LoC TessdataManagerTests, 86 LoC PeriodicCaptureThreadTests, 56 LoC PropertyEditorFactoryTests

### Folge-Aktionen (offene Spec-Updates)

Diese Doku-Eintragung referenziert die I-23 bis I-25 Decisions; die zugehÃ¶rigen
Specs ( 005,  006,  009) werden in einem Folge-Cluster mit-diffed.