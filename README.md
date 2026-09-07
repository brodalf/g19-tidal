# G19Tidal

Zeigt den laufenden TIDAL-Track auf dem Farbdisplay einer **Logitech G19 / G19s** —
Cover, Titel, Interpret, Album, Fortschrittsbalken. Die Tasten unter dem Display
steuern die Wiedergabe.

TIDAL bringt keine LCD-Unterstützung mit und die mitgelieferte
`LCDMedia.exe` der Logitech Gaming Software kennt nur Alt-Player wie Winamp,
iTunes und den Windows Media Player. Dieses Applet umgeht das, indem es die
**System Media Transport Controls (SMTC)** von Windows ausliest — dort meldet
sich TIDAL wie jeder saubere Windows-Player an.

```
+------------------------------------------+
|  * TIDAL                        PLAYING  |
|  ---------------------------------------- |
|  +----------+   Titel des Songs           |
|  |          |   Interpret                 |
|  |  Cover   |   Album                     |
|  +----------+                             |
|                                           |
|  ==================------------------      |
|  1:47                              4:12   |
+------------------------------------------+
```

## Funktionen

- Cover als scharfes Thumbnail **und** als weichgezeichneter, abgedunkelter Hintergrund
- Laufschrift für Titel/Interpret/Album, wenn der Text nicht passt
- Flüssiger Fortschrittsbalken — die Position wird zwischen den SMTC-Updates interpoliert,
  sonst ruckelt der Balken im Sekundentakt
- Transportsteuerung über die Displaytasten: **links** = zurück, **rechts** = weiter, **OK** = Play/Pause
- `--any` zeigt statt TIDAL die gerade aktive Medienquelle (Spotify, Browser, ...)

## Voraussetzungen

| | |
|---|---|
| Betriebssystem | Windows 10 1809 oder neuer |
| Hardware | Logitech G19 oder G19s, **mit angeschlossenem Netzteil** |
| Software | Logitech Gaming Software (LGS) — nicht G HUB, das unterstützt den G19 nicht |
| Build | .NET 8 SDK |

Das Applet lädt `LogitechLcd.dll` aus dem LGS-SDK-Ordner:

```
C:\Program Files\Logitech Gaming Software\SDK\LCD\x64\LogitechLcd.dll
```

Liegt LGS woanders, kann der Pfad über die Umgebungsvariable `G19_LCD_SDK`
gesetzt werden.

## Bauen und starten

```powershell
dotnet build -c Release
dotnet run -c Release
```

Oder das fertige Binary:

```powershell
dotnet publish -c Release -o out
.\out\G19Tidal.exe
```

Das Applet erscheint danach in der Applet-Rotation des G19 und lässt sich mit den
Menü-Tasten der Tastatur auswählen.

## Autostart

Verknüpfung zu `G19Tidal.exe` in den Autostart-Ordner legen:

```powershell
explorer shell:startup
```

Für einen Start ohne Konsolenfenster in der `.csproj` `<OutputType>Exe</OutputType>`
auf `WinExe` ändern und neu bauen.

## Aufbau

| Datei | Zweck |
|---|---|
| `src/LogitechLcd.cs` | P/Invoke auf `LogitechLcd.dll`, inklusive Auflösung des SDK-Pfads |
| `src/MediaWatcher.cs` | SMTC-Polling, Cover-Abruf, Transportsteuerung |
| `src/NowPlaying.cs` | Unveränderlicher Snapshot des Abspielzustands |
| `src/LcdRenderer.cs` | Zeichnet die 320×240-Oberfläche und schiebt sie ans Display |
| `src/Program.cs` | Render-Schleife, Tastenauswertung, Lebenszyklus |

### Zwei Fallstricke der SDK-Anbindung

Beide kosten sonst einen Nachmittag Fehlersuche:

1. **Rückgabewerte.** Das SDK gibt C++-`bool` zurück (ein Byte). Das
   Standard-Marshalling von .NET erwartet den vier Byte großen Win32-`BOOL` und liest
   damit Müll. Jeder Einsprungpunkt braucht `[return: MarshalAs(UnmanagedType.I1)]`.
2. **Pixelformat.** `LogiLcdColorSetBackground` will 320 × 240 × 4 Byte in **BGRA**.
   Das entspricht auf Little-Endian exakt dem Speicherlayout von
   `PixelFormat.Format32bppArgb` — der Puffer lässt sich also direkt aus `LockBits`
   kopieren, ohne die Kanäle zu tauschen.

## Lizenz

MIT — siehe [LICENSE](LICENSE).

Nicht mit Logitech oder TIDAL verbunden.
