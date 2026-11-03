using System.Text.RegularExpressions;

namespace Icom_Web_Control.Services.RadioMenu;

/// <summary>One item line of a radio menu file.</summary>
/// <param name="Line">1-based line number, for messages.</param>
/// <param name="Section">The [section] it sits under, e.g. "SET > Function > TX Delay".</param>
/// <param name="Name">The item's name, left of "=".</param>
/// <param name="Value">What the user wants, right of "=" and before ";".</param>
/// <param name="Address">The 1A 05 item number from the comment, e.g. "00 29", if there is one.</param>
/// <param name="SavedRaw">The data bytes the radio answered when the file was saved, if known.</param>
public sealed record RadioMenuFileItem(int Line, string Section, string Name, string Value, string? Address, string? SavedRaw);

/// <summary>A radio menu file as read back: the [File] header and the item lines.</summary>
public sealed record RadioMenuParsedFile(string? Program, string? Radio, IReadOnlyList<RadioMenuFileItem> Items);

/// <summary>
/// Reads a radio menu .ini and turns each value back into the data bytes
/// 1A 05 sends.
///
/// The value as the radio shows it wins: a user edits "Beep Level = 30%" and
/// never needs to touch the bytes in the comment. The saved bytes are only
/// used when the shown value is still what they decode to, so an unedited
/// line goes back exactly as it was read.
///
/// Between the page and the server an item's value is its data bytes in hex
/// ("00 01"), except a text item's, which is the text.
/// </summary>
public static class RadioMenuLoad
{
    private static readonly Regex SectionLine = new(@"^\[([^\]]*)\]$", RegexOptions.Compiled);
    private static readonly Regex Note = new(
        @"^\s*(\d{2} \d{2})(?:\s*=\s*([0-9A-Fa-f]{2}(?: [0-9A-Fa-f]{2})*)?)?\s*(?:\[TX\])?\s*$", RegexOptions.Compiled);
    private static readonly Regex NumberWithUnit = new(@"^([+-]?\d+(?:\.\d+)?)\s*[A-Za-z/%]+$", RegexOptions.Compiled);

    public const string NoAnswer = "(no answer)";
    public const string Blank = "(blank)";

    public static RadioMenuParsedFile Parse(string text)
    {
        string? program = null, radio = null;
        string section = "";
        var items = new List<RadioMenuFileItem>();
        var lines = text.TrimStart('﻿').Split('\n');
        for (int n = 0; n < lines.Length; n++)
        {
            var line = lines[n].Trim();
            if (line.Length == 0 || line[0] == ';' || line[0] == '#') continue;
            var m = SectionLine.Match(line);
            if (m.Success)
            {
                section = m.Groups[1].Value.Trim();
                continue;
            }
            int semi = line.IndexOf(';');
            var left = semi < 0 ? line : line[..semi];
            var comment = semi < 0 ? "" : line[(semi + 1)..];
            int eq = left.IndexOf('=');
            if (eq < 0) continue;
            var name = left[..eq].Trim();
            var value = left[(eq + 1)..].Trim();
            if (section.Equals("File", StringComparison.OrdinalIgnoreCase))
            {
                if (name.Equals("Program", StringComparison.OrdinalIgnoreCase)) program = value;
                else if (name.Equals("Radio", StringComparison.OrdinalIgnoreCase)) radio = value;
                continue;
            }
            var note = Note.Match(comment);
            string? address = note.Success ? note.Groups[1].Value : null;
            string? raw = note.Success && note.Groups[2].Success && note.Groups[2].Value.Length > 0
                ? note.Groups[2].Value.ToUpperInvariant() : null;
            items.Add(new RadioMenuFileItem(n + 1, section, name, value, address, raw));
        }
        return new RadioMenuParsedFile(program, radio, items);
    }

    /// <summary>The table item a file line is for: by item number, else by section and name.</summary>
    public static RadioMenuItem? Find(IReadOnlyList<RadioMenuItem> table, RadioMenuFileItem line)
    {
        if (line.Address is not null)
        {
            var byAddress = table.FirstOrDefault(i => i.Address == line.Address);
            if (byAddress is not null) return byAddress;
        }
        return table.FirstOrDefault(i =>
            Same(RadioMenuFile.SectionOf(i), line.Section) && Same(i.Function, line.Name));
    }

    /// <summary>
    /// The value to send for <paramref name="text"/>, or null with a reason.
    /// <paramref name="current"/> is the radio's answer now.
    /// </summary>
    public static string? Encode(RadioMenuItem item, string text, string? savedRaw, byte[] current, out string? error)
    {
        error = null;
        if (item.Fixed is not null) return Fail(out error, item.Fixed);
        text = text.Trim();
        if (item.IsText) return CheckText(item, text, out error);

        var codec = item.Codec;
        // The saved bytes, when the line has not been edited and they still fit the radio.
        if (savedRaw is not null && Bytes(savedRaw) is { } saved && Acceptable(item, saved, current)
            && Matches(text, item.Decode(saved), loose: false))
            return RadioMenuFile.Hex(saved);
        // Already what the radio has: keep its bytes, so nothing is sent.
        if (Matches(text, item.Decode(current), loose: false) || Matches(text, item.Decode(current), loose: true))
            return RadioMenuFile.Hex(current);

        if (codec.Values is not null)
        {
            var hit = codec.Values.FirstOrDefault(v => Matches(text, item.Decode(v), loose: false))
                   ?? codec.Values.FirstOrDefault(v => Matches(text, item.Decode(v), loose: true));
            if (hit is not null && hit.Length == current.Length) return RadioMenuFile.Hex(hit);
            var shown = codec.Values.Select(item.Decode).Distinct().ToList();
            var list = shown.Count <= 12
                ? string.Join(", ", shown)
                : $"{shown[0]}, {shown[1]}, {shown[2]} … {shown[^1]}";
            return Fail(out error, $"'{text}' is not a setting the radio offers. It takes: {list}.");
        }
        if (codec.Parse is not null)
        {
            var parsed = codec.Parse(text);
            if (parsed is not null && Acceptable(item, parsed, current)) return RadioMenuFile.Hex(parsed);
            return Fail(out error, $"'{text}' is not in the form the radio shows, such as {item.Decode(current)}.");
        }
        return Fail(out error, "IWC can only put this item back as it was saved. Change it on the radio.");
    }

    /// <summary>
    /// Every value the item takes, as (value, label), one per label: where
    /// several values show the same (256 steps shown as 0–100%), the radio's
    /// current value stands for its label so choosing it changes nothing.
    /// Null for an item the editor cannot set.
    /// </summary>
    public static IReadOnlyList<(string Raw, string Label)>? Choices(RadioMenuItem item, byte[] current)
    {
        if (item.Fixed is not null || item.Codec.Values is null) return null;
        var now = item.Decode(current);
        var list = new List<(string, string)>();
        var seen = new HashSet<string>();
        if (!item.Codec.Values.Any(v => v.AsSpan().SequenceEqual(current)))
            list.Add((RadioMenuFile.Hex(current), now)); // not one the table lists, but what the radio has
        foreach (var v in item.Codec.Values)
        {
            if (v.Length != current.Length) continue;
            var label = item.Decode(v);
            if (!seen.Add(label)) continue;
            list.Add((label == now ? RadioMenuFile.Hex(current) : RadioMenuFile.Hex(v), label));
        }
        return list.DistinctBy(c => c.Item1).ToList();
    }

    /// <summary>
    /// Checks a value sent back by the page before it goes to the radio and
    /// returns the data bytes to send. The page could send anything, so this
    /// is the last word, not the preview.
    /// </summary>
    public static byte[]? CheckRaw(RadioMenuItem item, string raw, byte[] current, out string? error)
    {
        error = null;
        if (item.Fixed is not null) { error = item.Fixed; return null; }
        if (item.IsText)
            return CheckText(item, raw.Trim(), out error) is { } t ? TextBytes(item, t, current) : null;
        if (Bytes(raw) is { } data && Acceptable(item, data, current)) return data;
        error = "That value does not fit the radio now, so nothing was sent. Preview the file again.";
        return null;
    }

    /// <summary>
    /// A value Load may send: the length the radio answers in, one the table
    /// lists, or for an item without a list one its parser makes again or its
    /// decoder reads as the manual gives (a saved value going back as it was).
    /// </summary>
    private static bool Acceptable(RadioMenuItem item, byte[] data, byte[] current)
    {
        if (data.Length == 0 || data.Length != current.Length) return false;
        var codec = item.Codec;
        if (codec.Values is not null) return codec.Values.Any(v => v.AsSpan().SequenceEqual(data));
        var shown = item.Decode(data);
        if (codec.Parse is not null) return codec.Parse(shown) is { } back && back.AsSpan().SequenceEqual(data);
        return shown != RadioMenuFile.Hex(data);
    }

    private static string? CheckText(RadioMenuItem item, string text, out string? error)
    {
        error = null;
        var codec = item.Codec;
        if (text.Length == 0 || text.Equals(Blank, StringComparison.OrdinalIgnoreCase))
            return Fail(out error, "Load cannot blank a text item. Clear it on the radio.");
        var t = codec.Upper ? text.ToUpperInvariant() : text;
        if (t.Length > codec.TextMax) return Fail(out error, $"Too long: at most {codec.TextMax} characters.");
        if (!Regex.IsMatch(t, $"^[{codec.TextChars}]+$"))
            return Fail(out error, $"The radio takes only these characters here: {Readable(codec.TextChars!)}.");
        return t;
    }

    /// <summary>
    /// The bytes for a text value: padded with spaces to the full width when
    /// the radio answers padded, as it does for memory names, else as typed.
    /// </summary>
    private static byte[] TextBytes(RadioMenuItem item, string text, byte[] current)
    {
        var t = current.Length == item.Codec.TextMax ? text.PadRight(item.Codec.TextMax) : text;
        return System.Text.Encoding.ASCII.GetBytes(t);
    }

    /// <summary>True if the radio's answer after a set is the value sent.</summary>
    public static bool ReadBackMatches(RadioMenuItem item, byte[] sent, byte[]? answer)
    {
        if (answer is null) return false;
        if (item.IsText)
            return System.Text.Encoding.ASCII.GetString(answer).Trim() == System.Text.Encoding.ASCII.GetString(sent).Trim();
        return answer.AsSpan().SequenceEqual(sent);
    }

    /// <summary>A text item's value as the page holds it: the text, not the bytes.</summary>
    public static string TextOf(byte[] data) =>
        data.All(b => b is >= 0x20 and < 0x7F) ? System.Text.Encoding.ASCII.GetString(data).Trim() : "";

    /// <summary>"00 01 FF" → bytes, or null if it is not hex bytes.</summary>
    public static byte[]? Bytes(string raw)
    {
        var parts = raw.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        if (parts.Length == 0) return null;
        var data = new byte[parts.Length];
        for (int i = 0; i < parts.Length; i++)
            if (parts[i].Length != 2 || !byte.TryParse(parts[i], System.Globalization.NumberStyles.HexNumber, null, out data[i]))
                return null;
        return data;
    }

    /// <summary>"A-Z0-9/@.-" → "A to Z, 0 to 9, /, @, ., -". A '-' is a range only between two characters.</summary>
    private static string Readable(string chars)
    {
        var parts = new List<string>();
        for (int i = 0; i < chars.Length; i++)
        {
            if (i + 2 < chars.Length && chars[i + 1] == '-')
            {
                parts.Add($"{chars[i]} to {chars[i + 2]}");
                i += 2;
            }
            else parts.Add(chars[i].ToString());
        }
        return string.Join(", ", parts);
    }

    /// <summary>
    /// Case, spaces and a leading "+" do not count. Loose also lets the unit
    /// go: "50" matches "50%" and "10" matches "10 sec".
    /// </summary>
    private static bool Matches(string text, string shown, bool loose)
    {
        var t = Norm(text);
        if (t == Norm(shown)) return true;
        if (!loose) return false;
        var m = NumberWithUnit.Match(shown.Trim());
        return m.Success && t == Norm(m.Groups[1].Value);
    }

    private static string Norm(string s)
    {
        var t = new string(s.Where(c => !char.IsWhiteSpace(c)).ToArray()).ToLowerInvariant();
        return t.StartsWith('+') ? t[1..] : t;
    }

    private static bool Same(string a, string b) => Norm(a) == Norm(b);

    private static string? Fail(out string? error, string message)
    {
        error = message;
        return null;
    }
}
