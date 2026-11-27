namespace Icom_Web_Control.Services.RadioMenu;

/// <summary>The newest radio menu file for this radio: its name, when it was written, and its text.</summary>
public sealed record RadioMenuSaveInfo(string Name, DateTime Modified, string Text);

/// <summary>One front-panel level that is not what the newest file holds.</summary>
public sealed record RadioMenuChange(string Name, string Now, string Saved);

/// <summary>
/// What the reminder should say. Kind is one of:
/// none (no file yet), changed (a level differs from the newest file),
/// old (the newest file is <see cref="RadioMenuReminder.OldAfterDays"/> days old or more),
/// noknobs (the newest file was saved before IWC saved the front panel levels),
/// ok (nothing to say).
/// </summary>
public sealed record RadioMenuReminderResult(string Kind, int? Days, string? File, IReadOnlyList<RadioMenuChange> Changes);

/// <summary>
/// Decides whether to remind the user to save the radio menu, and whether
/// closing IWC should ask to save first. Pure: the controller finds the
/// newest file and reads the levels, so this needs no radio and no disk.
///
/// The IC-7300MK2 keeps one of each level for every mode, so reading them
/// now covers everything; no mode change is ever needed. Menu items are not
/// compared (reading them all takes seconds); the age check covers changes
/// made on the radio's own menu.
/// </summary>
public static class RadioMenuReminder
{
    public const int OldAfterDays = 30;

    /// <param name="newest">The newest file for this radio, or null if there is none.</param>
    /// <param name="live">The levels read now, by knob address (knob:MIC, knob:AGC@USB, ...); null where the radio did not answer.</param>
    public static RadioMenuReminderResult Check(RadioMenuSaveInfo? newest, DateTime now, string? model,
        IReadOnlyDictionary<string, int?> live)
    {
        if (newest is null)
            return new("none", null, null, Array.Empty<RadioMenuChange>());

        int days = Days(newest, now);
        var file = RadioMenuLoad.Parse(newest.Text);
        var table = RadioKnobs.TableFor(model);
        var changes = Compare(file, model, live);
        bool hasKnobs = file.Items.Any(i => RadioKnobs.IsKnobAddress(i.Address));

        string kind = changes.Count > 0 ? "changed"
            : days >= OldAfterDays ? "old"
            : table is not null && !hasKnobs ? "noknobs"
            : "ok";
        return new(kind, days, newest.Name, changes);
    }

    /// <summary>
    /// The levels that differ from the file. A level the file has no line
    /// for, or the radio did not answer, is not a change. A per-mode or
    /// per-band level is only compared on the mode or band it was saved for:
    /// the live reading's address says where it was read.
    /// </summary>
    public static List<RadioMenuChange> Compare(RadioMenuParsedFile file, string? model, IReadOnlyDictionary<string, int?> live)
    {
        var changes = new List<RadioMenuChange>();
        if (RadioKnobs.TableFor(model) is null) return changes;
        var saved = file.Items.Where(i => RadioKnobs.IsKnobAddress(i.Address))
            .GroupBy(i => i.Address!).Select(g => g.First());
        foreach (var line in saved)
        {
            if (RadioKnobs.ByAddress(line.Address, model, out var where) is not { } knob) continue;
            if (!live.TryGetValue(line.Address!, out var value) || value is not int now) continue;
            // The value as the file shows it: what Load would set, even if edited by hand.
            if (RadioKnobs.Encode(knob, line.Value, out _) is not int was || knob.Same(was, now)) continue;
            changes.Add(new(RadioKnobs.Label(knob, where), knob.Show(now), knob.Show(was)));
        }
        return changes;
    }

    /// <summary>
    /// Closing IWC: should it ask to save first? Only when a front-panel level
    /// read just now is not what the newest file holds. With no file at all
    /// it does not ask: the start of IWC already offers the first save.
    /// </summary>
    /// <param name="live">The levels read just now; empty if they could not be read.</param>
    public static (bool Ask, string Message, IReadOnlyList<RadioMenuChange> Changes) ExitCheck(
        RadioMenuSaveInfo? newest, DateTime now, string? model, IReadOnlyDictionary<string, int?> live)
    {
        if (newest is null || RadioKnobs.TableFor(model) is null)
            return (false, "", Array.Empty<RadioMenuChange>());
        var changes = Compare(RadioMenuLoad.Parse(newest.Text), model, live);
        if (changes.Count == 0) return (false, "", changes);
        int days = Days(newest, now);
        return (true, $"Since your last save ({Ago(days)}), {Describe(changes)}.", changes);
    }

    /// <summary>"MIC GAIN changed from 50% to 30%, COMP changed from OFF to ON"</summary>
    public static string Describe(IEnumerable<RadioMenuChange> changes) =>
        string.Join(", ", changes.Select(c => $"{c.Name} changed from {c.Saved} to {c.Now}"));

    /// <summary>One line for the banner and the Settings page.</summary>
    public static string Message(RadioMenuReminderResult r) => r.Kind switch
    {
        "none" => "Your radio's settings have not been saved yet. A saved copy lets you put the menu and the front panel settings back if they get changed.",
        "changed" => $"Since your last save ({Ago(r.Days ?? 0)}), {Describe(r.Changes)}.",
        "old" => $"Your last save of the radio's settings was {r.Days} days ago.",
        "noknobs" => "Your last save was made before IWC saved the front panel settings (RF GAIN, NB, MIC GAIN, RF POWER and so on). Save again to include them.",
        _ => "",
    };

    private static int Days(RadioMenuSaveInfo newest, DateTime now) => Math.Max(0, (int)(now - newest.Modified).TotalDays);

    private static string Ago(int days) => days == 0 ? "today" : $"{days} day{(days == 1 ? "" : "s")} ago";
}
