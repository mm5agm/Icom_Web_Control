namespace Icom_Web_Control.Services.RadioMenu;

/// <summary>
/// One item of the radio's SET menu, reached over CI-V as 1A 05 hi lo.
/// </summary>
/// <param name="Number">The item number, 1–270 on the IC-7300MK2; sent as two BCD bytes (0123 → 01 23).</param>
/// <param name="Section">The menu the item lives under, e.g. "SET" or "SCOPE SET".</param>
/// <param name="Group">The path inside it, e.g. "Function > TX Delay".</param>
/// <param name="Function">The item's name as the radio shows it.</param>
/// <param name="Decode">Turns the reply's data bytes into what the radio displays.</param>
/// <param name="Tx">True when the item changes how or when the radio transmits.
/// Not used by Save; it marks the items a later Load must not write unasked.</param>
public sealed record RadioMenuItem(
    int Number,
    string Section,
    string Group,
    string Function,
    Func<byte[], string> Decode,
    bool Tx = false)
{
    /// <summary>The item as the CI-V manual prints it, e.g. "01 23".</summary>
    public string Address => $"{Number / 100:00} {Number % 100:00}";
}

/// <summary>An item and the data the radio answered with (null if it did not).</summary>
public sealed record RadioMenuReading(RadioMenuItem Item, byte[]? Data)
{
    public string Value => Data is null ? "" : Item.Decode(Data);
    public string Raw => Data is null ? "" : string.Join(" ", Data.Select(b => b.ToString("X2")));
}

/// <summary>Picks the menu table for a model and writes the save file.</summary>
public static class RadioMenuFile
{
    /// <summary>The menu table for a model, or null if IWC has none for it yet.
    /// The original IC-7300 numbers its items differently, so it is not this table.</summary>
    public static IReadOnlyList<RadioMenuItem>? TableFor(string? radioModel) => radioModel switch
    {
        "IC-7300MK2" => Ic7300Mk2Menu.Items,
        _ => null,
    };

    /// <summary>
    /// The save file, an .ini that reads in Notepad: one [section] per menu
    /// group and one line per item, the value as the radio shows it, then a
    /// comment with the 1A 05 item number and the reply's data bytes so a
    /// later Load can put each item back exactly.
    /// </summary>
    public static string ToIni(string radioModel, DateTime takenLocal, IEnumerable<RadioMenuReading> readings)
    {
        var sb = new System.Text.StringBuilder();
        sb.AppendLine("; IWC radio menu: the radio's own SET menu.")
          .AppendLine("; Each line is: item = value as the radio shows it   ; 1A 05 item = what the radio answered")
          .AppendLine("; [TX] marks items that change how or when the radio transmits.")
          .AppendLine()
          .AppendLine("[File]")
          .AppendLine("Program = IWC")
          .AppendLine($"Radio = {radioModel}")
          .AppendLine($"Saved = {takenLocal:yyyy-MM-dd HH:mm}");
        string? header = null;
        foreach (var r in readings)
        {
            var h = string.IsNullOrEmpty(r.Item.Group) ? $"[{r.Item.Section}]" : $"[{r.Item.Section} > {r.Item.Group}]";
            if (h != header)
            {
                sb.AppendLine().AppendLine(h);
                header = h;
            }
            var line = $"{r.Item.Function} = {(r.Data is null ? "(no answer)" : r.Value)}";
            var note = r.Data is null ? r.Item.Address : $"{r.Item.Address} = {r.Raw}";
            sb.Append(line.PadRight(40)).Append(" ; ").Append(note);
            if (r.Item.Tx) sb.Append("  [TX]");
            sb.AppendLine();
        }
        return sb.ToString();
    }
}
