namespace G19Tidal;

internal static class Program
{
    private const int TargetFps = 15;

    private static MediaWatcher _watcher = null!;

    private static int Main(string[] args)
    {
        if (args.Contains("--help") || args.Contains("-h"))
        {
            PrintUsage();
            return 0;
        }

        var anySource = args.Contains("--any");

        Console.WriteLine("G19Tidal - TIDAL auf dem Logitech G19 LCD");
        Console.WriteLine(anySource
            ? "Quelle: beliebiger Media-Player (--any)"
            : "Quelle: TIDAL");
        Console.WriteLine();

        try
        {
            LogitechLcd.Initialize();
        }
        catch (DllNotFoundException ex)
        {
            Console.Error.WriteLine(ex.Message);
            return 2;
        }

        if (!LogitechLcd.LogiLcdInit("TIDAL", LogitechLcd.TypeColor))
        {
            Console.Error.WriteLine(
                "LogiLcdInit fehlgeschlagen. Laeuft die Logitech Gaming Software (LCore.exe)?");
            return 3;
        }

        using var cts = new CancellationTokenSource();
        Console.CancelKeyPress += (_, e) =>
        {
            e.Cancel = true;
            cts.Cancel();
        };

        _watcher = new MediaWatcher(anySource);
        var poller = Task.Run(() => _watcher.RunAsync(cts.Token), cts.Token);

        Console.WriteLine("Laeuft. Tasten am Display: links = zurueck, rechts = weiter, OK = Play/Pause.");
        Console.WriteLine("Beenden mit Strg+C.");
        Console.WriteLine();

        try
        {
            RenderLoop(cts.Token);
        }
        finally
        {
            cts.Cancel();
            try { poller.Wait(TimeSpan.FromSeconds(2)); } catch { /* shutting down anyway */ }
            LogitechLcd.LogiLcdShutdown();
            Console.WriteLine();
            Console.WriteLine("Beendet.");
        }

        return 0;
    }

    private static void RenderLoop(CancellationToken ct)
    {
        using var renderer = new LcdRenderer();

        var frameTime = TimeSpan.FromSeconds(1.0 / TargetFps);
        var buttons = new ButtonReader();
        var lastStatus = "";
        var warnedDisconnected = false;

        while (!ct.IsCancellationRequested)
        {
            var frameStart = DateTime.UtcNow;

            if (!LogitechLcd.LogiLcdIsConnected(LogitechLcd.TypeColor))
            {
                if (!warnedDisconnected)
                {
                    Console.WriteLine("Warte auf das G19-Display ...");
                    warnedDisconnected = true;
                }

                Sleep(TimeSpan.FromSeconds(1), ct);
                continue;
            }

            if (warnedDisconnected)
            {
                Console.WriteLine("Display verbunden.");
                warnedDisconnected = false;
            }

            HandleButtons(buttons);

            var np = _watcher.Current;
            renderer.Render(np);

            var status = np.HasSession && np.Title.Length > 0
                ? $"{(np.IsPlaying ? "|>" : "||")} {np.Artist} - {np.Title}"
                : "-- nichts wird abgespielt --";

            if (status != lastStatus)
            {
                Console.WriteLine($"[{DateTime.Now:HH:mm:ss}] {status}");
                lastStatus = status;
            }

            var elapsed = DateTime.UtcNow - frameStart;
            var remaining = frameTime - elapsed;
            if (remaining > TimeSpan.Zero) Sleep(remaining, ct);
        }
    }

    private static void HandleButtons(ButtonReader buttons)
    {
        // Fire and forget: a transport call can block for a moment on the SMTC broker and
        // must never stall the render loop.
        if (buttons.WasPressed(LogitechLcd.ButtonLeft)) _ = _watcher.PreviousAsync();
        if (buttons.WasPressed(LogitechLcd.ButtonRight)) _ = _watcher.NextAsync();
        if (buttons.WasPressed(LogitechLcd.ButtonOk)) _ = _watcher.TogglePlayPauseAsync();
    }

    private static void Sleep(TimeSpan duration, CancellationToken ct)
    {
        try { ct.WaitHandle.WaitOne(duration); }
        catch (ObjectDisposedException) { /* cancelled during shutdown */ }
    }

    /// <summary>
    /// Turns the SDK's level-triggered button state into edge-triggered events, so holding
    /// a button skips one track rather than the whole playlist.
    /// </summary>
    private sealed class ButtonReader
    {
        private readonly HashSet<int> _down = new();

        public bool WasPressed(int button)
        {
            var isDown = LogitechLcd.LogiLcdIsButtonPressed(button);

            if (isDown) return _down.Add(button);

            _down.Remove(button);
            return false;
        }
    }

    private static void PrintUsage()
    {
        Console.WriteLine("""
            G19Tidal - zeigt den laufenden TIDAL-Track auf dem Logitech G19 LCD.

              G19Tidal.exe [--any]

              --any     Beliebige Medienquelle anzeigen, nicht nur TIDAL
                        (Spotify, Firefox, YouTube im Browser, ...)
              --help    Diese Hilfe

            Voraussetzungen:
              * Logitech Gaming Software laeuft (LCore.exe)
              * G19 mit angeschlossenem Netzteil, Display erkannt

            Tasten unter dem Display:
              links = vorheriger Titel, rechts = naechster Titel, OK = Play/Pause
            """);
    }
}
