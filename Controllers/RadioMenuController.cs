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
        private readonly ILogger<RadioMenuController> _logger;

        public RadioMenuController(IRadioController radio, ISettingsService settings, ILogger<RadioMenuController> logger)
        {
            _radio = radio;
            _settings = settings;
            _logger = logger;
        }

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

            var now = DateTime.Now;
            var ini = RadioMenuFile.ToIni(model, now, readings);
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
            return Ok(new { name, path, missing, unanswered });
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
            foreach (var line in file.Items)
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

            string? warning = file.Radio is not null && !string.Equals(file.Radio, model, StringComparison.OrdinalIgnoreCase)
                ? $"This file was saved from a {file.Radio}; the radio now is a {model}. Check each change."
                : null;
            _logger.LogInformation("Radio menu load preview: {Rows} lines, {Changes} changes", rows.Count, changes);
            return Ok(new { model, fileRadio = file.Radio, warning, rows });
        }

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
            try
            {
                var results = new List<object>();
                foreach (var want in request.Items.DistinctBy(i => i.Address))
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
