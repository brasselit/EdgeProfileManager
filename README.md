# Edge Profile Manager

Ein Tray- und Taskbar-Tool, um Edge-Profile aller Channels (Stable, Beta, Dev, Canary) mit einem Klick zu starten oder zu deren Fenster zu wechseln.

## Funktionen (Stand Phase 1 und 2)

- **Tray-Menü:** Ein Klick (links oder rechts) zeigt alle Profile, gruppiert gemäss `config.json`.
  - Profile mit offenem Fenster erscheinen **fett**.
  - Klick auf ein Profil: Ist ein Fenster offen, wird es aktiviert, sonst wird das Profil gestartet.
  - Ist das aktive Fenster bereits von diesem Profil, wechselt ein erneuter Klick zum nächsten Fenster desselben Profils.
  - **Shift+Klick** öffnet immer ein neues Fenster.
- **Taskbar und Start:** Die Startmenü-Verknüpfung „Edge Profile Manager“ anheften. Am einfachsten über **Einstellungen › An Start anheften…** bzw. **An Taskleiste anheften…** im Tray-Menü: Diese Punkte markieren die Verknüpfung im Explorer und zeigen, welchen Rechtsklick-Befehl du wählst. Windows lässt Programme nicht selbst anheften.
  - Linksklick auf die Schaltfläche zeigt das Profilmenü an der Maus.
  - Rechtsklick zeigt die **Jump List** mit den Profilen.
- **Virtuelle Desktops:** Pro Profil lässt sich ein Desktop festlegen (siehe `desktop` / `desktopMode`). Dafür wird `VirtualDesktopAccessor.dll` benötigt; ohne die DLL ist die Funktion deaktiviert.
- **Live-Aktualisierung:**
  - Änderungen an `config.json` werden sofort übernommen.
  - Neue oder umbenannte Edge-Profile werden erkannt, weil das Tool die `Local State`-Dateien überwacht.

## Profile und Menü verwalten (Phasen 3–5)

Tray-Menü › **Profile und Menü verwalten…** oder `EdgeProfileManager.exe --manage` öffnet das Menü-Board:

- **Spalten** sind die Gruppen des Tray-Menüs: links „Hauptmenü“ (Profile ohne Gruppe), rechts „Nicht zugeordnet“.
- **Karten** sind Profile, mit Profilfarbe, Avatar, Instanz, zugewiesenem Desktop und dem Hinweis „läuft“.
- **Karte ziehen:**
  - in eine andere Spalte: Gruppe wechseln
  - auf eine Karte: davor einsortieren
  - nach „Nicht zugeordnet“: aus dem Menü entfernen
- **Gruppen:** Das Menü ⋯ einer Spalte bietet Umbenennen, Verschieben, Trennlinie, Untergruppe und Löschen. Neue Gruppen legst du mit **+ Gruppe** an.
- Oben lässt sich die Ansicht auf eine Instanz filtern.
- Jede Änderung wird sofort in `config.json` gespeichert; Tray-Menü und Jump List folgen direkt.

Ein Klick auf eine Karte zeigt rechts die Details, getrennt in zwei Abschnitte.

**Edge-Profil** – ändert Edge selbst, Edge muss dafür geschlossen sein (das Tool fragt nach):

| Aktion | Beschreibung |
|---|---|
| Umbenennen, Avatar | Edit-Button neben Name bzw. Avatar; Änderungen in `Preferences` und `Local State` |
| **Farbe** | Setzt das Farbdesign des Profils (wie „Darstellung › Farbe“ in Edge) sowie die Avatar- und Hervorhebungsfarbe. Auswahl aus einer Palette oder frei; „Keine Farbe“ stellt das Standarddesign wieder her. |
| Übertragen ⇄ | Kopieren in eine andere Instanz (neues oder vorhandenes Zielprofil) oder **geprüft verschieben**: Jede Datei wird mit SHA256 geprüft, die Quelle erst danach gelöscht. Menüeinträge werden automatisch umgestellt. |
| Öffnen, Neues Fenster | Profil starten bzw. zum Fenster wechseln (auch per Doppelklick auf die Karte) |
| Backup, Profilordner, Löschen | Backup als ZIP mit `meta.json`, kompatibel zum Script. Löschen erst nach Rückfrage. |

**Menü-Eintrag** – ändert nur `config.json`: Gruppe, Bezeichnung (leer = Edge-Name), Desktop, Verhalten bei offenem Fenster, Jump List, „Aus Menü entfernen“. Bei nicht zugeordneten Profilen steht hier „Aufnehmen in …“.

In der Kopfzeile:
- **+ Neues Profil**: Name, E-Mail und optional ein PNG-Avatar; die E-Mail landet in `customer-profile.json`
- **Wiederherstellen**
- **Backup …**: Umfang alle Instanzen, Instanz des ausgewählten Profils oder nur das ausgewählte Profil
- **Einstellungen** (Zahnrad): Standardverhalten und Jump List

Diese Optionen gelten für Backup, Restore und Übertragen:
- **Nur lokale Daten**: überträgt Local Storage, IndexedDB usw. statt des ganzen Profils.
- **Caches einschliessen**
- **Quelle darf laufen**: erlaubt Backup oder Kopie bei geöffnetem Edge.

Läuft eine betroffene Edge-Instanz, fragt das Tool nach, ob sie beendet werden soll. Es schliesst zuerst die Fenster regulär und beendet danach verbleibende Prozesse.

## Bauen und starten

```powershell
dotnet build
.\src\EdgeProfileManager\bin\Debug\net10.0-windows\EdgeProfileManager.exe
```

Für einen festen Installationsort:

```powershell
dotnet publish src/EdgeProfileManager -c Release -o "$env:LOCALAPPDATA\Programs\EdgeProfileManager"
```

Danach von dort starten. Die Startmenü-Verknüpfung wird dabei automatisch auf den neuen Pfad gesetzt. Autostart schaltest du im Tray unter **Einstellungen › Mit Windows starten** ein.

### Befehlszeile

| Aufruf | Wirkung |
|---|---|
| *(ohne Argumente)* | Profilmenü an der Maus anzeigen (startet bei Bedarf im Tray) |
| `--background` | Nur im Tray starten (wird für Autostart verwendet) |
| `--manage` | Verwaltungsfenster öffnen |
| `--open "Stable/Profile 1" [--new-window]` | Profil aktivieren oder starten (wird von der Jump List verwendet) |
| `--dump-windows` | Edge-Fenster mit erkanntem Profil, Desktop-Status und AUMID ausgeben |
| `--reload`, `--exit` | Konfiguration neu laden bzw. Tray-Instanz beenden |

Läuft bereits eine Instanz, wird der Befehl über eine Named Pipe an sie weitergegeben.

## Konfiguration

Datei: `%APPDATA%\EdgeProfileManager\config.json`. Beim ersten Start wird sie mit einer Gruppe pro Channel erzeugt. Kommentare (`//`) und nachgestellte Kommas sind erlaubt.

```jsonc
{
  "defaultDesktopMode": "switchTo",   // switchTo | bringHere | moveToAssigned
  "showUnassigned": true,             // nicht eingetragene Profile unter "Nicht zugeordnet"
  "jumpListUnassigned": false,        // ... und auch in der Jump List
  "jumpListStyle": "categories",      // categories | tasks
  "menu": [
    { "group": "Kunden", "children": [
      { "profile": "Stable/Profile 3", "label": "Kunde A", "desktop": 2 },
      { "group": "Archiv", "children": [ { "profile": "Beta/Profile 1", "jumpList": false } ] }
    ]},
    { "separator": true },
    { "profile": "Stable/Default", "desktop": "Privat", "desktopMode": "switchTo" }
  ]
}
```

- **`profile`**: `Channel/Ordner`, z.B. `Stable/Default` oder `Beta/Profile 4`. Der Ordner bleibt auch nach einem Umbenennen gleich. Die Referenzen zeigt **Einstellungen › Fenster-Diagnose** bzw. `--dump-windows`.
- **`label`**: Anzeigename im Menü. Ohne Angabe wird der Edge-Profilname verwendet.
- **`desktop`**: Nummer (ab 1) oder Name des virtuellen Desktops.
- **`desktopMode`**: Was passiert, wenn bereits ein Fenster offen ist:
  - `switchTo`: zum Desktop wechseln, auf dem das Fenster liegt
  - `bringHere`: Fenster auf den aktuellen Desktop holen
  - `moveToAssigned`: Fenster auf den zugewiesenen `desktop` verschieben und dorthin wechseln. Das ist der Standard, wenn `desktop` gesetzt ist.

  Neue Fenster werden immer auf den zugewiesenen `desktop` verschoben.
- **`jumpList`**: `false` blendet das Profil in der Jump List aus.

### Hinweise zur Jump List

- Jump Lists kennen keine Untermenüs. Verschachtelte Gruppen erscheinen deshalb als Kategorie „Gruppe › Untergruppe“.
- Mit dem Stil `categories` zeigt Windows pro Liste nur rund 10 Einträge. Ausserdem muss die Option **Einstellungen › Personalisierung › Start › „Zuletzt geöffnete Elemente … in Sprunglisten anzeigen“** aktiv sein.
- `"jumpListStyle": "tasks"` zeigt alle Profile ungruppiert im Abschnitt „Aufgaben“. Dieser Stil ist von beiden Einschränkungen nicht betroffen.

## Virtuelle Desktops

Windows bietet keine öffentliche API, um Fenster anderer Programme zwischen Desktops zu verschieben. Deshalb wird [VirtualDesktopAccessor](https://github.com/Ciantic/VirtualDesktopAccessor) verwendet:

- Datei: `lib/VirtualDesktopAccessor.dll`, Release `2024-12-16-windows11` für 24H2
- SHA256: `8740c572a1c000e3b87ffeb1e4c397eae9af3bd4a2abdc3bcffacab4493f8ff5`
- Wird beim Build automatisch neben die exe kopiert.

Nach einem grösseren Windows-Update kann eine neuere Version der DLL nötig sein. Den Status zeigt das Tray-Menü unter **Einstellungen**.

## Tests

```powershell
dotnet run --project tests/EdgeProfileManager.Tests            # alle Tests
dotnet run --project tests/EdgeProfileManager.Tests -- Config  # nur passende Tests
```

Das Testprojekt hat keine NuGet-Abhängigkeiten und bringt einen minimalen eigenen Runner mit.

## Aufbau

- `src/EdgeProfileManager.Core`: UI-freie Logik
  - Profilerkennung (Port von `Get-EdgeProfiles`)
  - Fenstersuche über AppUserModelID (`MSEdge`, `MSEdge.UserData.Profile1`, …)
  - Launcher, Konfiguration, virtuelle Desktops
- `src/EdgeProfileManager`: WPF/WinForms-Tray-App mit Tray-Menü, Jump List, Single Instance, Autostart und Startmenü-Verknüpfung

## Lizenz

[Apache License 2.0](LICENSE), Copyright 2026 brassel-it. Hinweise zu mitgelieferten Fremdkomponenten stehen in [THIRD-PARTY-NOTICES.md](THIRD-PARTY-NOTICES.md).

## Nächste Schritte

- **Phasen 3–5:** erledigt (siehe „Profile und Menü verwalten“).
- **Phase 6:** Erster Release mit Setup und GitHub-Repository.
