using System.Diagnostics;
using Microsoft.AspNetCore.Mvc;
using Icom_Web_Control.Services;
using Icom_Web_Control.Services.RadioMenu;

namespace Icom_Web_Control.Controllers
{
    /// <summary>
    /// Saves the radio's own SET menu to a text file that opens in Notepad,
    /// and loads it back. Save only reads, item by item with 1A 05. Load is
    /// two steps: preview reads the radio and shows what would change,
    /// writing nothing; apply writes only the items the user ticked, each
    /// checked again against the menu table and read back. Phase 8 of
    /// docs/design/iwc-clone-split-plan.md.
    ///
    /// The save also holds the front panel levels the SET menu does not
    /// (MIC GAIN, COMP, RF POWER and so on, see <see cref="RadioKnobs"/>), and
    /// the reminder and exit-check endpoints ask the user to save when those
    /// have changed or the last save is old.
    /// </summary>
    [ApiController]
    [Route("api/radiomenu")]
    public class RadioMenuController : ControllerBase
    {
        private const int Attempts = 3;
        private const int MaxFileChars = 256 * 1024;

        /// <summary>Where saves go: with the rest of IWC's files, so the user can find them.</summary>
        public static string SavesFolder => Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
            "MM5AGM", "Icom Web Control", "Radio menus");

        /// <summary>One load at a time, so two browsers cannot interleave writes.</summary>
        private static readonly SemaphoreSlim LoadGate = new(1, 1);

        private readonly IRadioController _radio;
        private readonly ISettingsService _settings;
        private readonly RadioStateService _state;
        private readonly ILogger<RadioMenuController> _logger;

        public RadioMenuController(IRadioController radio, ISettingsService settings, RadioStateService state, ILogger<RadioMenuController> logger)
        {
            _radio = radio;
            _settings = settings;
            _state = state;
            _logger = logger;
        }

        /// <summary>The mode and band of the VFO in use, for AGC (per mode) and PREAMP, ATT, IP+ (per band).</summary>
        private Task<RadioKnobPlace?> PlaceAsync(CancellationToken ct) =>
            RadioKnobs.ReadPlaceAsync(_radio, _state.ActiveVfo == 1 ? RadioVfo.B : RadioVfo.A, ct);

        /// <summary>
        /// POST /api/radiomenu/save — reads every menu item and writes an .ini file
        /// into <see cref="SavesFolder"/>, with IWC's other files, rather than
        /// downloading it: a page cannot choose where a browser's download goes,
        /// so the user could not be told where to find it.
        /// 270 reads on an IC-7300MK2, several seconds at 19200 baud.
        /// </summary>
        [HttpPost("save")]
        public async Task<IActionResult> Save(CancellationToken ct)
        {
            var (model, table, problem) = await TableAsync("Saving");
            if (problem is not null) return problem;

            var readings = new List<RadioMenuReading>(table!.Count);
            foreach (var item in table)
                readings.Add(new RadioMenuReading(item, await ReadAsync(item, ct)));

            int missing = readings.Count(r => r.Data is null);
            var unanswered = readings.Where(r => r.Data is null).Select(r => r.Item.Function).ToList();
            _logger.LogInformation("Radio menu save: {Read} of {Total} items read from the {Model}",
                readings.Count - missing, readings.Count, model);
            if (missing == readings.Count)
                return StatusCode(504, new { error = "The radio did not answer any menu reads." });

            // The front panel settings: one read each, for the mode and band the radio is on.
            var knobs = RadioKnobs.TableFor(model);
            var knobReadings = knobs is null ? new List<RadioKnobReading>()
                : await RadioKnobs.ReadAllAsync(_radio, knobs, await PlaceAsync(ct), ct);

            var now = DateTime.Now;
            var ini = RadioMenuFile.ToIni(model, now, readings) + (knobs is null ? "" : RadioKnobs.ToIni(knobReadings));
            var name = $"IWC radio menu {model} {now:yyyy-MM-dd HHmm}.ini";
            // UTF-8 with BOM so older Notepad shows [▲] and [▼] properly.
            var bytes = System.Text.Encoding.UTF8.GetPreamble().Concat(System.Text.Encoding.UTF8.GetBytes(ini)).ToArray();
            var path = Path.Combine(SavesFolder, name);
            try
            {
                Directory.CreateDirectory(SavesFolder);
                await System.IO.File.WriteAllBytesAsync(path, bytes, ct);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                _logger.LogError(ex, "Radio menu save: could not write {Path}", path);
                return StatusCode(500, new { error = $"Could not write {path}: {ex.Message}" });
            }
            _reminderCache = null;
            return Ok(new
            {
                name, path, missing, unanswered,
                knobs = knobReadings.Count(k => k.Value is not null),
                knobsTotal = knobReadings.Count,
                knobsUnanswered = knobReadings.Where(k => k.Value is null).Select(k => k.Label).ToList(),
            });
        }

        /// <summary>GET /api/radiomenu/files — the copies IWC kept, newest first.</summary>
        [HttpGet("files")]
        public IActionResult Files()
        {
            var dir = new DirectoryInfo(SavesFolder);
            var files = dir.Exists
                ? dir.GetFiles("*.ini").OrderByDescending(f => f.LastWriteTime)
                    .Select(f => new { name = f.Name, modified = f.LastWriteTime.ToString("yyyy-MM-dd HH:mm") }).ToList<object>()
                : new List<object>();
            return Ok(new { folder = SavesFolder, files });
        }

        /// <summary>
        /// GET /api/radiomenu/files/{name} — one saved file, as text, for Load.
        /// With ?download=true, as a download, for a browser on another PC.
        /// </summary>
        [HttpGet("files/{name}")]
        public async Task<IActionResult> ReadFile(string name, [FromQuery] bool download, CancellationToken ct)
        {
            // A bare file name in the folder, nothing that could climb out of it.
            if (name != Path.GetFileName(name) || !name.EndsWith(".ini", StringComparison.OrdinalIgnoreCase))
                return BadRequest(new { error = "Not a radio menu file name." });
            var path = Path.Combine(SavesFolder, name);
            if (!System.IO.File.Exists(path))
                return NotFound(new { error = $"{name} is not in {SavesFolder}." });
            if (download)
                return PhysicalFile(path, "text/plain", name);
            return Ok(new { name, text = await System.IO.File.ReadAllTextAsync(path, ct) });
        }

        /// <summary>
        /// POST /api/radiomenu/open-folder — opens the folder in Explorer on the PC
        /// IWC runs on. Created if missing so the user lands somewhere real.
        /// </summary>
        [HttpPost("open-folder")]
        public IActionResult OpenFolder()
        {
            try
            {
                Directory.CreateDirectory(SavesFolder);
                Process.Start(new ProcessStartInfo { FileName = SavesFolder, UseShellExecute = true });
                return Ok(new { path = SavesFolder });
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Radio menu: could not open {Folder}", SavesFolder);
                return StatusCode(500, new { error = ex.Message, path = SavesFolder });
            }
        }

        /// <summary>
        /// GET /api/radiomenu/current — reads every item for the Change radio
        /// settings editor: its value now and, for each item that can be
        /// changed, the choices the radio offers. Writes nothing.
        /// </summary>
        [HttpGet("current")]
        public async Task<IActionResult> Current(CancellationToken ct)
        {
            var (model, table, problem) = await TableAsync("Changing");
            if (problem is not null) return problem;

            var rows = new List<object>(table!.Count);
            int missing = 0;
            var unanswered = new List<string>();
            foreach (var item in table)
            {
                var data = await ReadAsync(item, ct);
                if (data is null)
                {
                    missing++;
                    unanswered.Add(item.Function);
                }
                var choices = data is null ? null : RadioMenuLoad.Choices(item, data);
                rows.Add(new
                {
                    address = item.Address,
                    section = RadioMenuFile.SectionOf(item),
                    function = item.Function,
                    raw = data is null ? null : item.IsText ? RadioMenuLoad.TextOf(data) : RadioMenuFile.Hex(data),
                    shown = data is null ? null : item.Decode(data),
                    tx = item.Tx,
                    link = item.Fixed is not null,
                    // Neither listed nor parsed (the scope edges, the split offsets): Load can only put these back as saved.
                    reason = item.Fixed ?? (item.Codec.Values is null && item.Codec.Parse is null && !item.IsText
                        ? "Change this on the radio. A saved file puts it back as it was saved, but it cannot be edited there."
                        : null),
                    textMax = data is null || item.Fixed is not null ? 0 : item.Codec.TextMax,
                    upper = item.Codec.Upper,
                    choices = choices?.Select(c => new { raw = c.Raw, label = c.Label }),
                });
            }
            if (missing == table.Count)
                return StatusCode(504, new { error = "The radio did not answer any menu reads." });
            return Ok(new { model, missing, unanswered, rows });
        }

        public sealed record PreviewRequest(string? Text);

        /// <summary>
        /// POST /api/radiomenu/load/preview — reads the radio and compares it with the file.
        /// Writes nothing. Each row says what would be sent, or why it cannot be.
        /// </summary>
        [HttpPost("load/preview")]
        public async Task<IActionResult> Preview([FromBody] PreviewRequest request, CancellationToken ct)
        {
            if (string.IsNullOrWhiteSpace(request.Text))
                return BadRequest(new { error = "The file is empty." });
            if (request.Text.Length > MaxFileChars)
                return BadRequest(new { error = "That file is too big to be a radio menu file." });
            var (model, table, problem) = await TableAsync("Loading");
            if (problem is not null) return problem;

            var file = RadioMenuLoad.Parse(request.Text);
            if (file.Items.Count == 0)
                return BadRequest(new { error = "No menu items found. Is this a radio menu file saved by IWC?" });

            var rows = new List<object>();
            var seen = new HashSet<string>();
            int changes = 0;
            foreach (var line in file.Items.Where(l => !RadioKnobs.IsKnobAddress(l.Address)))
            {
                var item = RadioMenuLoad.Find(table!, line);
                if (item is null)
                {
                    rows.Add(Row(null, line, "unknown", null, null, $"Line {line.Line}: not an item on the {model}."));
                    continue;
                }
                if (!seen.Add(item.Address))
                {
                    rows.Add(Row(item, line, "unknown", null, null, $"Line {line.Line}: this item is already in the file; the first one is used."));
                    continue;
                }
                if (line.Value.Equals(RadioMenuLoad.NoAnswer, StringComparison.OrdinalIgnoreCase))
                {
                    rows.Add(Row(item, line, "skipped", null, null, "The radio did not answer this item when the file was saved."));
                    continue;
                }
                if (item.Fixed is not null)
                {
                    rows.Add(Row(item, line, "blocked", null, null, item.Fixed));
                    continue;
                }
                var current = await ReadAsync(item, ct);
                if (current is null)
                {
                    rows.Add(Row(item, line, "noanswer", null, null, "The radio did not answer, so this cannot be checked."));
                    continue;
                }
                // Already what the radio shows: same, even where Load could not send it (a blank My Call).
                if (item.Decode(current) == line.Value.Trim())
                {
                    rows.Add(Row(item, line, "same", current, null, null));
                    continue;
                }
                var raw = RadioMenuLoad.Encode(item, line.Value, line.SavedRaw, current, out var error);
                var data = raw is null ? null : RadioMenuLoad.CheckRaw(item, raw, current, out error);
                if (data is null)
                    rows.Add(Row(item, line, "error", current, null, error));
                else if (RadioMenuLoad.ReadBackMatches(item, data, current))
                    rows.Add(Row(item, line, "same", current, raw, null, data));
                else
                {
                    rows.Add(Row(item, line, "change", current, raw, null, data));
                    changes++;
                }
            }

            changes += await PreviewKnobsAsync(file.Items.Where(l => RadioKnobs.IsKnobAddress(l.Address)).ToList(), model, rows, ct);

            string? warning = file.Radio is not null && !string.Equals(file.Radio, model, StringComparison.OrdinalIgnoreCase)
                ? $"This file was saved from a {file.Radio}; the radio now is a {model}. Check each change."
                : null;
            // An older file still loads; it just has no levels to put back.
            if (RadioKnobs.TableFor(model) is not null && !file.Items.Any(l => RadioKnobs.IsKnobAddress(l.Address)))
            {
                const string old = "This file was saved before IWC saved the front panel settings (RF GAIN, NB, MIC GAIN, RF POWER and so on), so only the menu is loaded. Save a new file to include them.";
                warning = warning is null ? old : $"{warning} {old}";
            }
            _logger.LogInformation("Radio menu load preview: {Rows} lines, {Changes} changes", rows.Count, changes);
            return Ok(new { model, fileRadio = file.Radio, warning, rows });
        }

        /// <summary>
        /// The front panel lines of a preview: each checked, then compared with
        /// what the radio has now. The transmit ones (RF POWER, MIC GAIN, ...)
        /// count as TX, so the page leaves them unticked. A per-mode or per-band
        /// line for another mode or band than the radio is on is skipped.
        /// </summary>
        private async Task<int> PreviewKnobsAsync(List<RadioMenuFileItem> lines, string model, List<object> rows, CancellationToken ct)
        {
            int changes = 0;
            var seen = new HashSet<string>();
            var place = lines.Count > 0 ? await PlaceAsync(ct) : null;
            foreach (var line in lines)
            {
                var knob = RadioKnobs.ByAddress(line.Address, model, out var where);
                if (knob is null)
                {
                    rows.Add(KnobRow(null, null, line, "unknown", null, null, $"Line {line.Line}: not a front panel setting IWC loads on the {model}."));
                    continue;
                }
                if (!seen.Add(line.Address!))
                {
                    rows.Add(KnobRow(knob, where, line, "unknown", null, null, $"Line {line.Line}: this setting is already in the file; the first one is used."));
                    continue;
                }
                if (line.Value.Equals(RadioMenuLoad.NoAnswer, StringComparison.OrdinalIgnoreCase))
                {
                    rows.Add(KnobRow(knob, where, line, "skipped", null, null, "The radio did not answer this setting when the file was saved."));
                    continue;
                }
                var value = RadioKnobs.Encode(knob, line.Value, out var error);
                if (value is null)
                {
                    rows.Add(KnobRow(knob, where, line, "error", null, null, error));
                    continue;
                }
                if (RadioKnobs.NotHere(knob, where, place) is { } elsewhere)
                {
                    rows.Add(KnobRow(knob, where, line, "skipped", null, null, elsewhere));
                    continue;
                }
                var current = await RadioKnobs.ReadAsync(_radio, knob, ct);
                if (current is null)
                    rows.Add(KnobRow(knob, where, line, "noanswer", null, null, "The radio did not answer, so this cannot be checked."));
                else if (knob.Same(current.Value, value.Value))
                    rows.Add(KnobRow(knob, where, line, "same", current, value, null));
                else
                {
                    rows.Add(KnobRow(knob, where, line, "change", current, value, null));
                    changes++;
                }
            }
            return changes;
        }

        private static object KnobRow(RadioKnob? knob, string? where, RadioMenuFileItem line, string status, int? current, int? wanted, string? message) => new
        {
            address = knob is null ? line.Address : RadioKnobs.Address(knob, where),
            section = line.Section,
            function = knob is null ? line.Name : RadioKnobs.Label(knob, where),
            current = current is int c && knob is not null ? knob.Show(c) : null,
            wanted = wanted is int v && knob is not null ? knob.Show(v) : line.Value,
            raw = wanted?.ToString(System.Globalization.CultureInfo.InvariantCulture),
            status,
            message,
            tx = knob?.Tx ?? true,
        };

        public sealed record ApplyItem(string Address, string Raw);
        public sealed record ApplyRequest(List<ApplyItem>? Items);

        /// <summary>
        /// POST /api/radiomenu/load/apply — writes the ticked items. Each is checked
        /// again against the table and the radio's current answer, sent, and read back.
        /// </summary>
        [HttpPost("load/apply")]
        public async Task<IActionResult> Apply([FromBody] ApplyRequest request, CancellationToken ct)
        {
            var (model, table, problem) = await TableAsync("Loading");
            if (problem is not null) return problem;
            if (request.Items is null || request.Items.Count == 0)
                return BadRequest(new { error = "Nothing was ticked." });
            if (!await LoadGate.WaitAsync(0, ct))
                return Conflict(new { error = "A radio menu load is already running." });
            _reminderCache = null;
            try
            {
                var results = new List<object>();
                var items = request.Items.DistinctBy(i => i.Address).ToList();
                var knobItems = items.Where(i => RadioKnobs.IsKnobAddress(i.Address)).ToList();
                var place = knobItems.Count > 0 ? await PlaceAsync(ct) : null;
                foreach (var want in knobItems)
                    results.Add(await ApplyKnobAsync(want, model, place, ct));
                foreach (var want in items.Where(i => !RadioKnobs.IsKnobAddress(i.Address)))
                {
                    var item = table!.FirstOrDefault(i => i.Address == want.Address);
                    if (item is null || string.IsNullOrEmpty(want.Raw))
                    {
                        results.Add(new { address = want.Address, function = (string?)null, now = (string?)null,
                            status = "error", message = "Not an item on this radio." });
                        continue;
                    }
                    var current = await ReadAsync(item, ct);
                    if (current is null)
                    {
                        results.Add(Result(item, "error", null, "The radio did not answer, so nothing was sent."));
                        continue;
                    }
                    var data = RadioMenuLoad.CheckRaw(item, want.Raw, current, out var error);
                    if (data is null)
                    {
                        results.Add(Result(item, "error", current, error));
                        continue;
                    }
                    if (RadioMenuLoad.ReadBackMatches(item, data, current))
                    {
                        results.Add(Result(item, "same", current, null));
                        continue;
                    }
                    _logger.LogInformation("Radio menu load: 1A 05 {Address} {Function} {From} -> {To}",
                        item.Address, item.Function, RadioMenuFile.Hex(current), RadioMenuFile.Hex(data));
                    var acked = await _radio.WriteMenuItemAsync(item.Number, data, ct);
                    var after = await ReadAsync(item, ct);
                    if (RadioMenuLoad.ReadBackMatches(item, data, after))
                        results.Add(Result(item, "ok", after, null));
                    else
                        results.Add(Result(item, "mismatch", after,
                            acked == false ? "The radio refused this value."
                            : after is null ? "Sent, but the radio did not answer the read back."
                            : $"Sent, but the radio now shows {item.Decode(after)}."));
                }
                return Ok(new { results });
            }
            finally
            {
                LoadGate.Release();
            }
        }

        /// <summary>One front panel setting from Load: checked, set only if it differs, and read back.</summary>
        private async Task<object> ApplyKnobAsync(ApplyItem want, string model, RadioKnobPlace? place, CancellationToken ct)
        {
            var knob = RadioKnobs.ByAddress(want.Address, model, out var where);
            if (knob is null)
                return new { address = want.Address, function = (string?)null, now = (string?)null,
                    status = "error", message = "Not a front panel setting on this radio." };
            if (!int.TryParse(want.Raw, System.Globalization.NumberStyles.None, System.Globalization.CultureInfo.InvariantCulture, out var value)
                || !RadioKnobs.IsValue(knob, value))
                return KnobResult(knob, where, "error", null, $"That is not a {knob.Name} setting.");
            if (RadioKnobs.NotHere(knob, where, place) is { } elsewhere)
                return KnobResult(knob, where, "error", null, elsewhere + " Nothing was sent.");
            var current = await RadioKnobs.ReadAsync(_radio, knob, ct);
            if (current is null)
                return KnobResult(knob, where, "error", null, "The radio did not answer, so nothing was sent.");
            if (knob.Same(current.Value, value))
                return KnobResult(knob, where, "same", current, null);
            _logger.LogInformation("Radio menu load: {Knob} {From} -> {To}", RadioKnobs.Label(knob, where), knob.Show(current.Value), knob.Show(value));
            bool acked = await RadioKnobs.WriteAsync(_radio, knob, value, ct);
            var after = await RadioKnobs.ReadAsync(_radio, knob, ct);
            // A level shown as % or Hz goes to the radio as 0-255, and some only take certain steps.
            bool ok = after is int a && knob.Same(a, value);
            return ok ? KnobResult(knob, where, "ok", after, null)
                : KnobResult(knob, where, "mismatch", after, !acked && after is int same && same == current
                    ? "The radio refused this value."
                    : after is null ? "Sent, but the radio did not answer the read back."
                    : $"Sent, but the radio now shows {knob.Show(after.Value)}.");
        }

        private static object KnobResult(RadioKnob knob, string? where, string status, int? now, string? message) => new
        {
            address = RadioKnobs.Address(knob, where),
            function = (string?)RadioKnobs.Label(knob, where),
            now = now is int v ? knob.Show(v) : null,
            status,
            message,
        };

        /// <summary>"Not now" on the reminder: hides it until IWC starts again.</summary>
        private static bool _reminderDismissedThisRun;

        /// <summary>The last check, so a page change does not read the radio again.</summary>
        private static (DateTime At, RadioMenuReminderResult Result)? _reminderCache;

        private static readonly TimeSpan ReminderCacheFor = TimeSpan.FromSeconds(60);

        /// <summary>"Not now" on an old or changed reminder also keeps it away for this long.</summary>
        public const int ReminderSnoozeDays = 7;

        private static string SnoozeFile => Path.Combine(SavesFolder, "reminder-snooze.txt");

        /// <summary>The newest file IWC saved for this radio model, or null.</summary>
        private static RadioMenuSaveInfo? NewestSave(string model)
        {
            var dir = new DirectoryInfo(SavesFolder);
            if (!dir.Exists) return null;
            // Newest first; a few only, so a folder of hundreds stays quick.
            foreach (var f in dir.GetFiles("*.ini").OrderByDescending(f => f.LastWriteTime).Take(20))
            {
                string text;
                try { text = System.IO.File.ReadAllText(f.FullName); }
                catch (IOException) { continue; }
                catch (UnauthorizedAccessException) { continue; }
                if (string.Equals(RadioMenuLoad.Parse(text).Radio, model, StringComparison.OrdinalIgnoreCase))
                    return new RadioMenuSaveInfo(f.Name, f.LastWriteTime, text);
            }
            return null;
        }

        private static DateTime? SnoozedUntil()
        {
            try
            {
                return System.IO.File.Exists(SnoozeFile)
                    && DateTime.TryParse(System.IO.File.ReadAllText(SnoozeFile).Trim(), System.Globalization.CultureInfo.InvariantCulture,
                        System.Globalization.DateTimeStyles.RoundtripKind, out var until)
                    ? until : null;
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                return null;
            }
        }

        /// <summary>
        /// The front panel settings read now, by knob address (knob:MIC,
        /// knob:AGC@USB). Reads only. Null if a load is running (the gate is busy).
        /// </summary>
        private async Task<Dictionary<string, int?>?> ReadLevelsNowAsync(IReadOnlyList<RadioKnob> knobs, CancellationToken ct)
        {
            if (!await LoadGate.WaitAsync(0, ct)) return null;
            try
            {
                var readings = await RadioKnobs.ReadAllAsync(_radio, knobs, await PlaceAsync(ct), ct);
                return readings.GroupBy(r => r.Address).ToDictionary(g => g.Key, g => g.First().Value);
            }
            finally
            {
                LoadGate.Release();
            }
        }

        /// <summary>
        /// GET /api/radiomenu/reminder — should IWC remind the user to save the
        /// radio menu? Reads only the front panel levels, never a mode change.
        /// The banner on every page asks when IWC starts; the Settings page
        /// shows the same line. pending means ask again shortly (radio not
        /// connected, or a load is running).
        /// </summary>
        [HttpGet("reminder")]
        public async Task<IActionResult> Reminder(CancellationToken ct)
        {
            var model = (await _settings.GetSettingsAsync()).RadioModel;
            if (RadioMenuFile.TableFor(model) is null)
                return Ok(new { supported = false });
            if (!_radio.IsConnected)
                return Ok(new { supported = true, pending = true });

            RadioMenuReminderResult result;
            if (_reminderCache is { } cached && DateTime.Now - cached.At < ReminderCacheFor)
                result = cached.Result;
            else
            {
                var newest = NewestSave(model);
                var live = new Dictionary<string, int?>();
                var knobs = RadioKnobs.TableFor(model);
                if (newest is not null && knobs is not null)
                {
                    if (await ReadLevelsNowAsync(knobs, ct) is not { } read)
                        return Ok(new { supported = true, pending = true });
                    live = read;
                }
                result = RadioMenuReminder.Check(newest, DateTime.Now, model, live);
                _reminderCache = (DateTime.Now, result);
            }

            // No save at all is asked at every start; the others wait out a "Not now".
            bool snoozed = _reminderDismissedThisRun
                || (result.Kind != "none" && SnoozedUntil() is DateTime until && until > DateTime.Now);
            return Ok(new
            {
                supported = true,
                pending = false,
                kind = result.Kind,
                show = result.Kind != "ok" && !snoozed,
                message = RadioMenuReminder.Message(result),
                days = result.Days,
                file = result.File,
                changes = result.Changes,
            });
        }

        /// <summary>
        /// GET /api/radiomenu/exit-check — closing IWC from the tray: should it
        /// ask to save the radio menu first? Asks when a front panel level read
        /// now differs from the newest file. Reads only.
        /// </summary>
        [HttpGet("exit-check")]
        public async Task<IActionResult> ExitCheck(CancellationToken ct)
        {
            var model = (await _settings.GetSettingsAsync()).RadioModel;
            var knobs = RadioKnobs.TableFor(model);
            if (RadioMenuFile.TableFor(model) is null || knobs is null || !_radio.IsConnected)
                return Ok(new { ask = false, connected = _radio.IsConnected });

            IReadOnlyDictionary<string, int?> live = new Dictionary<string, int?>();
            try
            {
                using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
                cts.CancelAfter(TimeSpan.FromSeconds(6));
                if (await ReadLevelsNowAsync(knobs, cts.Token) is { } read)
                    live = read;
            }
            catch (OperationCanceledException) when (!ct.IsCancellationRequested)
            {
                // Slow radio: nothing to compare, so do not ask.
            }

            var (ask, message, changes) = RadioMenuReminder.ExitCheck(NewestSave(model), DateTime.Now, model, live);
            return Ok(new { ask, message, changes, connected = true });
        }

        /// <summary>
        /// POST /api/radiomenu/reminder/later — "Not now": no banner until IWC
        /// starts again, and no old/changed reminder for a week. A missing save
        /// is still asked at the next start.
        /// </summary>
        [HttpPost("reminder/later")]
        public IActionResult ReminderLater()
        {
            _reminderDismissedThisRun = true;
            try
            {
                Directory.CreateDirectory(SavesFolder);
                System.IO.File.WriteAllText(SnoozeFile, DateTime.Now.AddDays(ReminderSnoozeDays).ToString("o"));
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                _logger.LogWarning(ex, "Radio menu reminder: could not write {Path}", SnoozeFile);
            }
            return Ok(new { snoozeDays = ReminderSnoozeDays });
        }

        /// <summary>The model and its menu table, or the reason there is none to use.</summary>
        private async Task<(string Model, IReadOnlyList<RadioMenuItem>? Table, IActionResult? Problem)> TableAsync(string doing)
        {
            var model = (await _settings.GetSettingsAsync()).RadioModel;
            var table = RadioMenuFile.TableFor(model);
            if (table is null)
                return (model, null, BadRequest(new { error = $"{doing} the radio menu is not available for the {model} yet." }));
            if (!_radio.IsConnected)
                return (model, null, BadRequest(new { error = "The radio is not connected." }));
            return (model, table, null);
        }

        private static object Row(RadioMenuItem? item, RadioMenuFileItem line, string status, byte[]? current,
            string? raw, string? message, byte[]? data = null) => new
        {
            address = item?.Address ?? line.Address,
            section = item is null ? line.Section : RadioMenuFile.SectionOf(item),
            function = item?.Function ?? line.Name,
            current = current is null || item is null ? null : item.Decode(current),
            wanted = data is null || item is null ? line.Value : item.Decode(data),
            raw,
            status,
            message,
            tx = item?.Tx ?? false,
        };

        private static object Result(RadioMenuItem item, string status, byte[]? now, string? message) => new
        {
            address = item.Address,
            function = (string?)item.Function,
            now = now is null ? null : item.Decode(now),
            status,
            message,
        };

        private async Task<byte[]?> ReadAsync(RadioMenuItem item, CancellationToken ct)
        {
            for (int i = 0; i < Attempts; i++)
            {
                var data = await _radio.ReadMenuItemAsync(item.Number, ct);
                if (data is not null) return data;
            }
            _logger.LogWarning("Radio menu: no answer for 1A 05 {Address}", item.Address);
            return null;
        }
    }
}
