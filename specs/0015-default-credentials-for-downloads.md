# 0015 — Default-Credentials für HTTP-Downloads

> **Status:** 🟡 **GEPLANT v0.1 (2026-07-10)**
> **Owner:** Martin
> **Abhängig von:** Spec 0012 (tessdata-Download)
> **Use-Case:** Corporate-Netzwerk mit NTLM-/Kerberos-Proxy

## Ziel

Wenn die TrayApp oder CLI HTTP-Downloads gegen das öffentliche Internet
ausführt (z. B. tessdata, später ggf. LLM-Modelle, App-Updates), soll der
`HttpClient` automatisch die **Windows-Default-Credentials** mitschicken,
damit ein ggf. vorhandener Firmen-Proxy (NTLM/Kerberos) ohne separate
User-Interaktion authentifiziert wird.

In einem typischen Unternehmens-Setup ist der Proxy über `netsh winhttp show proxy`
oder via WPAD/PAC konfiguriert; der `HttpClient` (mit aktivem `DefaultProxy`)
leitet die Anfrage durch den Proxy, scheitert aber ohne Credentials mit
`HTTP 407 Proxy Authentication Required`, wenn der Proxy Negotiate/NTLM fordert.

## Scope

| Komponente | Use-Case | Default-Credentials nötig? |
|---|---|---|
| `TessdataManager` (Spec 0012) | Download von `tessdata_fast` GitHub | **JA** — öffentliches Internet, Firmen-Proxy üblich |
| Künftige Downloads (LLM-Modelle, App-Updates, …) | öffentliches Internet | **JA** |
| `DeepgramTranscriptionProvider` | REST-API zu Deepgram | NEIN — Auth via `Authorization: Token …`, nicht Proxy |
| `AzureSpeechTranscriptionProvider` | REST-/WebSocket zu Azure | NEIN — Auth via Subscription-Key/OAuth |
| `ChromeDevToolsProtocolClient` | `localhost:9222` (Edge DevTools) | NEIN — kein Proxy involviert |
| `TeamsCdpReader` | `localhost:9223` (Teams-WebView2 DevTools) | NEIN — kein Proxy involviert |

## API-Änderung

**`src\AiRecall.Core\Tessdata\TessdataManager.cs`**:

```csharp
// Vorher:
public TessdataManager() : this(new HttpClientHandler(), DefaultBaseUrl) { }

// Nachher:
public TessdataManager()
    : this(CreateDefaultHandler(), DefaultBaseUrl) { }

private static HttpClientHandler CreateDefaultHandler()
{
    // UseProxy = true ist Default — System-Proxy-Erkennung (WPAD/PAC) bleibt aktiv.
    // UseDefaultCredentials schickt die Windows-Anmeldeinformationen für die
    // Ziel-Server-Auth mit (NTLM/Kerberos/Basic gegen den Zielserver).
    // DefaultProxyCredentials schickt Credentials speziell für den Proxy.
    var handler = new HttpClientHandler
    {
        UseDefaultCredentials = true,
        DefaultProxyCredentials = CredentialCache.DefaultCredentials,
    };
    return handler;
}
```

DI-Konstruktor (`TessdataManager(HttpMessageHandler, string)`) bleibt
unverändert — Tests injizieren weiterhin ihren Mock-Handler.

## Tests

- **`TessdataManagerTests.cs`** NEU:
  - `DefaultConstructor_HttpHandler_HasUseDefaultCredentialsEnabled`
    → erzeugt `new TessdataManager()`, holt via Reflection den internen
    `HttpClient._handler` (oder nutzt einen Test-Only-`HttpMessageHandler`-Wrapper),
    castet nach `HttpClientHandler`, prüft `UseDefaultCredentials == true`.
  - `DefaultConstructor_HttpHandler_HasDefaultProxyCredentialsSet`
    → prüft `DefaultProxyCredentials != null` (CredentialCache.DefaultCredentials).
- Bestehende 10 Tests laufen weiter durch, weil sie den DI-Konstruktor mit
  eigenem `StubHttpMessageHandler` nutzen.

## Konvention (für künftige Internet-Downloads)

Jeder neue HTTP-Client, der eine **externe URL** (nicht localhost) spricht,
muss `UseDefaultCredentials = true` + `DefaultProxyCredentials = CredentialCache.DefaultCredentials`
setzen — entweder direkt oder über einen privaten `CreateDefaultHandler()`-Helper.

API-Key-basierte Auth (Deepgram, Azure, GitHub-Token) bleibt davon
unberührt — `Authorization`-Header hat Vorrang vor Default-Credentials.

## Verworfen

- **Expliziter Proxy-URL/Port in User-Config**: macht manuelle Konfiguration
  nötig; das Default-Proxy-Discovery reicht für die meisten Setups.
- **WebProxy mit eigener URL**: gleicher Grund.
- **IHttpClientFactory / Microsoft.Extensions.Http**: zusätzliche Dependency,
  lohnt erst bei mehreren HttpClients mit Policies (Retry, Logging-Pipeline).
- **Credential-Persistenz**: Windows verwaltet Default-Credentials bereits
  im Credential Manager; AiRecall speichert keine Credentials.
- **Andere Auth-Schemes (Basic ohne SSL)**: nicht relevant — öffentliche
  Downloads (GitHub) sind TLS-geschützt; Credentials werden im NTLM-Tunnel
  gesendet.

## Out of Scope (v0.1)

- **Manuelle Proxy-URL/Password-Override** in Settings (Spec 0009).
- **PAC-Script-Debug-Output**: bei Proxy-Problemen kann User
  `netsh winhttp show proxy`/`set proxy` selbst prüfen.
- **Test-Mock für NTLM-Proxy**: out-of-scope, weil das ein
  Netzwerk-/Enterprise-Setup-Issue ist, nicht ein App-Code-Pfad.

## Offene Punkte

- [ ] Martin: Bestätigen, dass nur `TessdataManager` für Default-Credentials
      angefasst werden soll (kein Refactor für CDP-Clients).
- [ ] Martin: Soll der `CreateDefaultHandler()`-Helper exportiert werden
      (z. B. `AiRecall.Core.Net.HttpClientFactory.CreateDefaultHandler()`),
      damit künftige Download-Module ihn wiederverwenden können?
      Mein Vorschlag: **JA**, weil sonst die Konvention schwer durchsetzbar ist.
- [ ] Falls Punkt 2 JA: Datei `src/AiRecall.Core\Net\HttpClientFactory.cs` neu,
      mit `internal static` (nur intern im AiRecall.Core nutzbar — Tests
      via `InternalsVisibleTo`).