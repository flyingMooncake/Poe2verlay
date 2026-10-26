namespace Poe2Verlay;

/// <summary>Farming session start / stop, as an overlay panel.</summary>
internal static class ReportPanels
{
    static OverlayPanel Panel(Rectangle client, Color accent)
    {
        var k = client.Height / 1080f;
        // Left of centre: clear of the inventory panel on the right.
        return new OverlayPanel
        {
            TopCenter = new PointF(client.Left + client.Width * 0.4f, client.Top + 80 * k),
            FontPx = Math.Clamp(17 * k, 14, 24),
            Accent = accent,
        };
    }

    public static OverlayPanel Session(string title, FarmSession session, PriceDb? db, AppSettings s, Rectangle client)
    {
        var p = Panel(client, Palette.Gold);
        p.Header.Add(new LabelLine { Text = title, Color = Palette.Gold, Bold = true, Size = 1.15f });
        if (session.Running || session.Played > TimeSpan.Zero) AddSessionLines(p.Header, session, db);
        p.Footer.Add(new LabelLine
        {
            Text = $"time only counts while you play: alt-tab or {s.SessionIdleSeconds} s without input (Esc menu, food) pauses it",
            Color = Palette.Dim, Size = 0.78f,
        });
        p.Footer.Add(new LabelLine { Text = $"profit = runs finished with {s.RunEndHotkey}  ·  {s.SessionHotkey} starts / stops  ·  saved in sessions.csv", Color = Palette.Dim, Size = 0.78f });
        return p;
    }

    static void AddSessionLines(List<LabelLine> lines, FarmSession session, PriceDb? db)
    {
        var hours = session.Played.TotalHours;
        lines.Add(new LabelLine
        {
            Text = $"Session: {Fmt.Range(session.ProfitMin, session.ProfitMax, signed: true)} div" +
                   (db != null ? $" ({Fmt.Range(session.ProfitMin * db.ChaosPerDivine, session.ProfitMax * db.ChaosPerDivine, signed: true)} c)" : "") +
                   (hours > 0.01 ? $"  ·  ≈ {Fmt.Range(session.ProfitMin / hours, session.ProfitMax / hours, signed: true)} div/h" : ""),
            Color = Palette.Gold, Bold = true, Size = 0.95f,
            Tag = session.Running && session.Paused ? "paused" : null, TagColor = Palette.Warn,
        });
        lines.Add(new LabelLine
        {
            Text = $"{Fmt.Duration(session.Played)} played  ·  {Fmt.Duration(session.PausedTime)} paused  ·  {session.Runs} run(s)  ·  {session.Maps} map(s)" +
                   (session.Maps > 0 ? $"  ·  ≈ {Fmt.Range(session.ProfitMin / session.Maps, session.ProfitMax / session.Maps, signed: true)} div/map" : "") +
                   $"  ·  since {session.Started:HH:mm}",
            Color = Palette.Text, Size = 0.85f,
        });
    }
}
