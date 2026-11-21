# Spec 0018 — .NET 10 und NuGet-Paketpflege

- **Status:** Implementiert und lokal verifiziert
- **Owner:** AI Recall
- **Datum:** 2026-11-21

Alle Projekte einschließlich `tools/EmojiIconGen` targeten `net10.0-windows`.
`global.json` setzt SDK 10.0.402 mit `latestFeature`-Rollforward; der Release-Workflow bezieht seine SDK-Version aus derselben Datei. CLI und Tray behalten ihre bisherigen Ausgabe- und Publish-Konventionen mit dem neuen TFM im lokalen Ausgabepfad.

## Direkte NuGet-Referenzen

Stabile gelistete Versionen auf nuget.org (Stand 2026-11-21):

| Paket | Version |
| --- | --- |
| `coverlet.collector` | 10.1.1 |
| `DocumentFormat.OpenXml` | 3.5.1 (unverändert) |
| `Microsoft.CognitiveServices.Speech` | 1.53.1 |
| `Microsoft.Extensions.Configuration.Json`, `Microsoft.Extensions.Logging` | 11.0.0 |
| `Microsoft.NET.Test.Sdk` | 18.12.0 |
| `NAudio`, `NAudio.Wasapi` | 3.3.0 |
| `PdfPig` | 0.1.16 |
| `ReverseMarkdown` | 6.3.0 |
| `Serilog` | 4.4.0 |
| `Serilog.Sinks.Console` | 6.1.1 |
| `Serilog.Sinks.File` | 7.0.0 |
| `System.Drawing.Common` | 11.0.0 |
| `Tesseract` | 5.2.0 (unverändert) |
| `xunit` | 2.9.3 |
| `xunit.runner.visualstudio` | 4.0.1 |

Die drei Microsoft-Pakete mit Version 11.0.0 liefern `net10.0`-Assets; sie erfordern kein .NET-11-SDK. `NAudio.Wasapi` 22.0.0 ist auf nuget.org nicht gelistet und gehört nicht zur 3.3.0-Paketfamilie. `PdfPig` ist die aktuelle offizielle Paket-ID des Upstream-Projekts; der Namespace `UglyToad.PdfPig` bleibt gleich. `xunit` bleibt bei der neuesten Version der bestehenden Paket-ID; xUnit v3 wäre ein separater Paket- und Framework-Wechsel.

## Verifikation

```powershell
dotnet restore AiRecall.sln
dotnet build AiRecall.sln -c Release --no-restore
dotnet build tools/EmojiIconGen/EmojiIconGen.csproj -c Release
dotnet test AiRecall.sln -c Release --no-build
```

Ergebnis: 851/851 Release-Tests; Tray-Publish und CLI-Hilfe erfolgreich. `dotnet package list --outdated` meldete keine neueren direkten Pakete, der NuGet-Audit einschließlich transitiver Abhängigkeiten keine bekannten Schwachstellen. Die WASAPI-Kompatibilitätsklassen sind unter NAudio 3.3.0 als veraltet markiert; Tests prüfen den Aufnahmevertrag mit Fakes, nicht mit echtem Mikrofon/Loopback.

Siehe [Release-Pipeline](0017-ci-cd-release-pipeline.md). Workboard: `1d9e2eb5-1bcb-45a1-90f9-27f968afbc0e`.
