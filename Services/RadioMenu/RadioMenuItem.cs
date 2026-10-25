namespace Icom_Web_Control.Services.RadioMenu;

/// <summary>
/// How one item's data bytes are shown and, for Load, made again.
/// </summary>
/// <param name="Decode">Turns the reply's data bytes into what the radio displays.</param>
/// <param name="Values">Every value the item takes, in the radio's order, when
/// there are few enough to list. Load and the editor only send one of these.</param>
/// <param name="Parse">For an item with too many values to list, turns what the
/// radio displays back into bytes, or null if the text is not one.</param>
/// <param name="TextMax">For a text item, the most characters it holds.</param>
/// <param name="TextChars">For a text item, the characters the radio accepts.</param>
/// <param name="Upper">For a text item, true when the radio takes capitals only.</param>
public sealed record RadioMenuCodec(
    Func<byte[], string> Decode,
    IReadOnlyList<byte[]>? Values = null,
    Func<string, byte[]?>? Parse = null,
    int TextMax = 0,
    string? TextChars = null,
    bool Upper = true);

/// <summary>
/// One item of the radio's SET menu, reached over CI-V as 1A 05 hi lo.
/// </summary>
/// <param name="Number">The item number, 1–270 on the IC-7300MK2; sent as two BCD bytes (0123 → 01 23).</param>
/// <param name="Section">The menu the item lives under, e.g. "SET" or "SCOPE SET".</param>
/// <param name="Group">The path inside it, e.g. "Function > TX Delay".</param>
/// <param name="Function">The item's name as the radio shows it.</param>
/// <param name="Codec">How its data bytes are shown and made.</param>
/// <param name="Tx">True when the item changes how or when the radio transmits.
/// Load sends these only when the user ticks them.</param>
/// <param name="Fixed">Why Load and the editor leave this item alone, or null if they can set it.</param>
public sealed record RadioMenuItem(
    int Number,
    string Section,
    string Group,
    string Function,
    RadioMenuCodec Codec,
    bool Tx = false,
    string? Fixed = null)
{
    /// <summary>The item as the CI-V manual prints it, e.g. "01 23".</summary>
    public string Address => $"{Number / 100:00} {Number % 100:00}";

    public string Decode(byte[] data) => Codec.Decode(data);

    public bool IsText => Codec.TextMax > 0;
}

/// <summary>An item and the data the radio answered with (null if it did not).</summary>
public sealed record RadioMenuReading(RadioMenuItem Item, byte[]? Data)
{
    public string Value => Data is null ? "" : Item.Decode(Data);
    public string Raw => Data is null ? "" : RadioMenuFile.Hex(Data);
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

    /// <summary>Data bytes as the file writes them: 00 01 02.</summary>
    public static string Hex(byte[] data) => string.Join(" ", data.Select(b => b.ToString("X2")));

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
          .AppendLine("; To change a setting, edit the value left of the ';' and load the file in IWC.")
          .AppendLine()
          .AppendLine("[File]")
          .AppendLine("Program = IWC")
          .AppendLine($"Radio = {radioModel}")
          .AppendLine($"Saved = {takenLocal:yyyy-MM-dd HH:mm}");
        string? header = null;
        foreach (var r in readings)
        {
            var h = $"[{SectionOf(r.Item)}]";
            if (h != header)
            {
                sb.AppendLine().AppendLine(h);
                header = h;
            }
            var line = $"{r.Item.Function} = {(r.Data is null ? RadioMenuLoad.NoAnswer : r.Value)}";
            var note = r.Data is null ? r.Item.Address : $"{r.Item.Address} = {r.Raw}";
            sb.Append(line.PadRight(40)).Append(" ; ").Append(note);
            if (r.Item.Tx) sb.Append("  [TX]");
            sb.AppendLine();
        }
        return sb.ToString();
    }

    /// <summary>The [section] an item is written under, e.g. "SET > Function > TX Delay".</summary>
    public static string SectionOf(RadioMenuItem item) =>
        string.IsNullOrEmpty(item.Group) ? item.Section : $"{item.Section} > {item.Group}";
}
