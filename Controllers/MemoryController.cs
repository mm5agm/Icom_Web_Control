using Microsoft.AspNetCore.Mvc;
using System.Text.Json;
using System.Text.Json.Serialization;
using Icom_Web_Control.Services;
using RadioWebControl.Core.Services; // AdifParser now lives in the shared core

namespace Icom_Web_Control.Controllers
{
    [ApiController]
    [Route("api/[controller]")]
    public class MemoryController : ControllerBase
    {
        private static readonly Dictionary<char, string> CodeToMode = new()
        {
            { '1', "LSB" }, { '2', "USB" }, { '3', "CW-U" }, { '4', "FM" },
            { '5', "AM" },  { '6', "RTTY-L" }, { '7', "CW-L" }, { '8', "DATA-L" },
            { '9', "RTTY-U" }, { 'A', "DATA-FM" }, { 'B', "FM-N" }, { 'C', "DATA-U" },
            { 'D', "AM-N" }, { 'E', "PSK" }, { 'F', "DATA-FM-N" }
        };

        private static readonly Dictionary<string, char> ModeToCode = new()
        {
            { "LSB", '1' }, { "USB", '2' }, { "CW-U", '3' }, { "FM", '4' },
            { "AM", '5' }, { "RTTY-L", '6' }, { "CW-L", '7' }, { "DATA-L", '8' },
            { "RTTY-U", '9' }, { "DATA-FM", 'A' }, { "FM-N", 'B' }, { "DATA-U", 'C' },
            { "AM-N", 'D' }, { "PSK", 'E' }, { "DATA-FM-N", 'F' }
        };

        private readonly MemoryService _memoryService;
        private readonly ISettingsService _settingsService;
        private readonly RadioStateService _radioStateService;
        private readonly ILogger<MemoryController> _logger;
        private readonly IWebHostEnvironment _env;
        private readonly MemoryBankService _bankService;
        private readonly IRadioController _radio;

        public MemoryController(
            MemoryService memoryService,
            ISettingsService settingsService,
            RadioStateService radioStateService,
            ILogger<MemoryController> logger,
            IWebHostEnvironment env,
            MemoryBankService bankService,
            IRadioController radio)
        {
            _memoryService = memoryService;
            _settingsService = settingsService;
            _radioStateService = radioStateService;
            _logger = logger;
            _env = env;
            _bankService = bankService;
            _radio = radio;
        }

        // ── CRUD ─────────────────────────────────────────────────────────────

        [HttpGet]
        public IActionResult GetAll() => Ok(_memoryService.GetAll());

        [HttpPost]
        public async Task<IActionResult> Create([FromBody] AppMemory memory)
        {
            if (string.IsNullOrWhiteSpace(memory.Mode)) memory.Mode = "USB";
            var created = await _memoryService.AddAsync(memory);
            return Ok(created);
        }

        [HttpPut("{id:int}")]
        public async Task<IActionResult> Update(int id, [FromBody] AppMemory memory)
        {
            memory.Id = id;
            if (!await _memoryService.UpdateAsync(memory))
                return NotFound();
            return Ok(memory);
        }

        [HttpDelete("{id:int}")]
        public async Task<IActionResult> Delete(int id)
        {
            if (!await _memoryService.DeleteAsync(id))
                return NotFound();
            return Ok();
        }

        // ── Recall (tune the active VFO to a memory) ────────────────────────

        [HttpPost("{id:int}/recall")]
        public async Task<IActionResult> Recall(int id)
        {
            var memory = _memoryService.GetById(id);
            if (memory == null) return NotFound();

            if (!_radio.IsConnected)
                return StatusCode(503, new { error = "Radio is not connected." });

            // Recall tunes the currently-active VFO to the stored memory. IWC
            // drives the IC-7300 through the CI-V seam (IRadioController), so the
            // two universal fields — mode and frequency — are pushed here and that
            // is what "recall a memory" means on this radio. The Yaesu-specific
            // extras the old CAT recall also sent (clarifier, antenna, roofing
            // filter, NB/NR/AGC, per-memory power) are not part of the IC-7300
            // memory model; they remain stored in the app memory but are not
            // applied. A later CI-V Memories block can extend this if wanted.
            var vfo = _radioStateService.ActiveVfo == 1 ? RadioVfo.B : RadioVfo.A;

            try
            {
                // Mode before frequency so the radio applies any mode-dependent
                // offset (e.g. CW pitch) before it tunes — avoids a small landing
                // error on the first read-back.
                if (!string.IsNullOrEmpty(memory.Mode))
                {
                    await _radio.SetModeAsync(vfo, memory.Mode, CancellationToken.None);
                    if (vfo == RadioVfo.B) _radioStateService.ModeB = memory.Mode;
                    else _radioStateService.ModeA = memory.Mode;
                    await Task.Delay(50);
                }

                await _radio.SetFrequencyHzAsync(vfo, memory.FrequencyHz, CancellationToken.None);
                if (vfo == RadioVfo.B) _radioStateService.FrequencyB = memory.FrequencyHz;
                else _radioStateService.FrequencyA = memory.FrequencyHz;

                _logger.LogInformation("Recalled memory {Id} ('{Label}') to VFO-{Vfo}: {Freq} Hz {Mode}",
                    id, memory.Label, vfo, memory.FrequencyHz, memory.Mode);

                return Ok(new { recalled = true, vfo = vfo.ToString(), frequencyHz = memory.FrequencyHz, mode = memory.Mode });
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Failed to recall memory {Id} to the radio", id);
                return StatusCode(500, new { error = "Failed to tune the radio to that memory." });
            }
        }

        // ── Save current VFO state as a new memory (with advanced fields) ────
        //
        // Reads the live radio state from RadioStateService so we capture
        // every field at the moment the user clicked "Save to Mem", rather
        // than asking the browser to round up scattered DOM values.
        public class SaveVfoRequest
        {
            public string Label { get; set; } = "";
            public string? Notes { get; set; }
        }

        [HttpPost("save-vfo/{vfo}")]
        public async Task<IActionResult> SaveVfo(string vfo, [FromBody] SaveVfoRequest? request)
        {
            bool isA = string.Equals(vfo, "A", StringComparison.OrdinalIgnoreCase);
            var mem = new AppMemory
            {
                Label             = string.IsNullOrWhiteSpace(request?.Label) ? "" : request!.Label.Trim(),
                FrequencyHz       = isA ? _radioStateService.FrequencyA : _radioStateService.FrequencyB,
                Mode              = (isA ? _radioStateService.ModeA : _radioStateService.ModeB) ?? "USB",
                ClarifierOffsetHz = isA ? _radioStateService.ClarifierOffsetA : _radioStateService.ClarifierOffsetB,
                RxClarOn          = _radioStateService.RxClarOn,
                TxClarOn          = _radioStateService.TxClarOn,
                Antenna           = isA ? _radioStateService.AntennaA : _radioStateService.AntennaB,
                IfWidthCode       = isA ? _radioStateService.IfWidthA : _radioStateService.IfWidthB,
                IfShiftHz         = isA ? _radioStateService.IfShiftA : _radioStateService.IfShiftB,
                RoofingCode       = isA ? _radioStateService.RoofingFilterA : _radioStateService.RoofingFilterB,
                NbOn              = (isA ? _radioStateService.NbA : _radioStateService.NbB) == "1",
                NbLevel           = isA ? _radioStateService.NbLevelA : _radioStateService.NbLevelB,
                NrLevel           = isA ? _radioStateService.NrA : _radioStateService.NrB,
                AgcMode           = isA ? _radioStateService.AgcA : _radioStateService.AgcB,
                PowerWatts        = _radioStateService.Power > 0 ? _radioStateService.Power : (int?)null,
                Notes             = request?.Notes,
            };
            var created = await _memoryService.AddAsync(mem);
            return Ok(created);
        }

        // ── Import from Radio ────────────────────────────────────────────────

        public class ImportRequest
        {
            public string Mode { get; set; } = "replace"; // "replace" or "merge"
        }

        // The IC-7300 has 99 internal memory channels, read/written whole over
        // CI-V 1A 00 (IRadioController.ReadMemoryChannelAsync / Write / Clear).
        // Only the three universal fields survive the round-trip — label (channel
        // name), frequency and mode — because that is all the radio's channel model
        // holds that maps to an app memory. The app's advanced fields (NB/NR/AGC,
        // per-memory power, notes) stay app-side. Memory read/write never keys TX.
        private const int RadioChannelCount = 99;

        [HttpPost("import-radio")]
        [RequestSizeLimit(1_000_000)]
        public async Task<IActionResult> ImportFromRadio([FromBody] ImportRequest request)
        {
            if (!_radio.IsConnected)
                return StatusCode(503, new { error = "Radio is not connected." });

            bool replace = string.Equals(request?.Mode, "replace", StringComparison.OrdinalIgnoreCase);

            var imported = new List<AppMemory>();
            int misses = 0;
            for (int ch = 1; ch <= RadioChannelCount; ch++)
            {
                var channel = await _radio.ReadMemoryChannelAsync(ch, CancellationToken.None);
                if (channel == null) { misses++; continue; }   // transaction miss — skip
                if (channel.IsEmpty || channel.FrequencyHz <= 0) continue;

                imported.Add(new AppMemory
                {
                    Label       = string.IsNullOrWhiteSpace(channel.Name) ? $"CH {ch:000}" : channel.Name.Trim(),
                    FrequencyHz = channel.FrequencyHz,
                    Mode        = string.IsNullOrWhiteSpace(channel.Mode) ? "USB" : channel.Mode,
                });
            }

            if (imported.Count == 0)
            {
                var reason = misses > 0
                    ? "The radio did not respond to memory reads. Check the connection and try again."
                    : "No programmed memory channels were found on the radio.";
                _logger.LogInformation("Import from radio found no channels ({Misses} misses)", misses);
                return Ok(new { imported = 0, warning = reason });
            }

            if (replace) await _memoryService.ReplaceAllAsync(imported);
            else         await _memoryService.MergeAsync(imported);

            _logger.LogInformation("Imported {Count} memory channels from the radio ({Mode})",
                imported.Count, replace ? "replace" : "merge");
            return Ok(new { imported = imported.Count });
        }

        // ── Export to Radio ──────────────────────────────────────────────────

        [HttpPost("export-radio")]
        public async Task<IActionResult> ExportToRadio()
        {
            if (!_radio.IsConnected)
                return StatusCode(503, new { error = "Radio is not connected." });

            // "Replace all": write app memories into channels 1..N, then clear the
            // remaining channels so the radio ends up matching the app exactly.
            var memories = _memoryService.GetAll()
                .Where(m => m.FrequencyHz > 0)
                .Take(RadioChannelCount)
                .ToList();

            int written = 0;
            for (int i = 0; i < memories.Count; i++)
            {
                var ok = await _radio.WriteMemoryChannelAsync(ToRadioChannel(memories[i], i + 1), CancellationToken.None);
                if (ok) written++;
            }
            for (int ch = memories.Count + 1; ch <= RadioChannelCount; ch++)
                await _radio.ClearMemoryChannelAsync(ch, CancellationToken.None);

            bool truncated = _memoryService.GetAll().Count(m => m.FrequencyHz > 0) > RadioChannelCount;
            _logger.LogInformation("Exported {Written} memories to the radio (replace all)", written);
            return Ok(new { written, truncated });
        }

        [HttpPost("export-radio-add")]
        public async Task<IActionResult> ExportToRadioAdd()
        {
            if (!_radio.IsConnected)
                return StatusCode(503, new { error = "Radio is not connected." });

            // Add into the radio's empty channels only, leaving programmed ones
            // untouched. Walk the channels, filling blanks from the app list.
            var memories = _memoryService.GetAll().Where(m => m.FrequencyHz > 0).ToList();
            int written = 0, next = 0;
            for (int ch = 1; ch <= RadioChannelCount && next < memories.Count; ch++)
            {
                var existing = await _radio.ReadMemoryChannelAsync(ch, CancellationToken.None);
                if (existing == null || !existing.IsEmpty) continue;   // skip misses and occupied channels
                if (await _radio.WriteMemoryChannelAsync(ToRadioChannel(memories[next], ch), CancellationToken.None))
                    written++;
                next++;
            }

            _logger.LogInformation("Added {Written} memories into the radio's empty channels", written);
            return Ok(new { written });
        }

        /// <summary>Map an app memory onto a radio channel (label→name capped at 16, split off, TX mirrors RX).</summary>
        private static RadioMemoryChannel ToRadioChannel(AppMemory m, int channel) => new()
        {
            Channel     = channel,
            FrequencyHz = m.FrequencyHz,
            Mode        = string.IsNullOrWhiteSpace(m.Mode) ? "USB" : m.Mode,
            Filter      = 1,
            Name        = (m.Label ?? "").Length > 16 ? m.Label!.Substring(0, 16) : (m.Label ?? ""),
        };

        // ── IWC starter bank (bundled with the app) ──────────────────────────
        //
        // The starter bank is a region-specific set of watering-hole memories
        // (FT8/FT4/SSB/CW/RTTY/beacons) shipped in wwwroot/data/starter-bank-*.json.
        // Three load modes are offered so the user can re-load after deleting
        // entries by accident without losing any customisations they've made:
        //
        //   add-missing  Only add entries whose labels aren't already present.
        //                Preserves edits AND restores deleted entries.
        //   append       Add every entry, even if duplicate labels result.
        //   replace      Wipe all existing memories and load the full bank.
        //                Frontend warns the user before invoking this mode.

        public class StarterBankFile
        {
            [JsonPropertyName("name")]        public string Name { get; set; } = "";
            [JsonPropertyName("description")] public string Description { get; set; } = "";
            [JsonPropertyName("entries")]     public List<AppMemory> Entries { get; set; } = new();
        }

        public class LoadStarterRequest
        {
            public string Mode { get; set; } = "add-missing"; // add-missing | append | replace
        }

        [HttpGet("starter-bank")]
        public async Task<IActionResult> GetStarterBank()
        {
            try
            {
                var bank = await LoadStarterBankFromDiskAsync();
                if (bank == null)
                    return NotFound(new { error = "Starter bank file not found for the current region." });
                return Ok(bank);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Failed to load starter bank file");
                return StatusCode(500, new { error = ex.Message });
            }
        }

        [HttpPost("starter-bank/load")]
        public async Task<IActionResult> LoadStarterBank([FromBody] LoadStarterRequest? request)
        {
            var mode = (request?.Mode ?? "add-missing").Trim().ToLowerInvariant();
            if (mode != "add-missing" && mode != "append" && mode != "replace")
                return BadRequest(new { error = $"Invalid mode '{mode}'. Expected add-missing, append or replace." });

            StarterBankFile? bank;
            try
            {
                bank = await LoadStarterBankFromDiskAsync();
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Failed to read starter bank file");
                return StatusCode(500, new { error = $"Failed to read starter bank: {ex.Message}" });
            }
            if (bank == null || bank.Entries.Count == 0)
                return NotFound(new { error = "Starter bank file not found or empty for the current region." });

            // Strip any IDs from the bank entries — IDs are assigned by MemoryService on insert.
            foreach (var e in bank.Entries) { e.Id = 0; e.SortOrder = 0; }

            try
            {
                int added;
                if (mode == "replace")
                {
                    await _memoryService.ReplaceAllAsync(new List<AppMemory>(bank.Entries));
                    added = bank.Entries.Count;
                }
                else if (mode == "append")
                {
                    await _memoryService.MergeAsync(new List<AppMemory>(bank.Entries));
                    added = bank.Entries.Count;
                }
                else // add-missing
                {
                    var existingLabels = new HashSet<string>(
                        _memoryService.GetAll().Select(m => (m.Label ?? "").Trim()),
                        StringComparer.OrdinalIgnoreCase);
                    var toAdd = bank.Entries
                        .Where(e => !existingLabels.Contains((e.Label ?? "").Trim()))
                        .ToList();
                    if (toAdd.Count > 0)
                        await _memoryService.MergeAsync(toAdd);
                    added = toAdd.Count;
                }

                return Ok(new
                {
                    mode,
                    added,
                    total = bank.Entries.Count,
                    bankName = bank.Name,
                    message = mode switch
                    {
                        "replace"     => $"Replaced all memories with {added} starter-bank entries.",
                        "append"      => $"Added {added} starter-bank entries (duplicates allowed).",
                        _             => added == 0
                            ? "No entries added — all starter-bank entries are already present (matched by label)."
                            : $"Added {added} missing starter-bank entries. Existing entries left untouched."
                    }
                });
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Failed to apply starter bank in mode {Mode}", mode);
                return StatusCode(500, new { error = $"Failed to apply starter bank: {ex.Message}" });
            }
        }

        // ── Themed starter banks ─────────────────────────────────────────────
        //
        // Split the bundled region starter bank into themed banks (FT8, FT4,
        // CW, SSB, RTTY, FM) and save each non-empty slice via MemoryBankService.
        // The user then sees them appear in the bank dropdown and can Load any
        // one to replace the current memories with that theme.
        //
        // Entries are tagged to exactly one theme — label-based for the data
        // modes (FT8/FT4), mode-based for the rest. Empty themes are skipped.

        public class CreateThemedBanksRequest
        {
            public bool Overwrite { get; set; } = false;
        }

        [HttpPost("starter-bank/create-themed-banks")]
        public async Task<IActionResult> CreateThemedStarterBanks([FromBody] CreateThemedBanksRequest? request)
        {
            var overwrite = request?.Overwrite ?? false;

            StarterBankFile? bank;
            try
            {
                bank = await LoadStarterBankFromDiskAsync();
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Failed to read starter bank file for themed split");
                return StatusCode(500, new { error = $"Failed to read starter bank: {ex.Message}" });
            }
            if (bank == null || bank.Entries.Count == 0)
                return NotFound(new { error = "Starter bank file not found or empty for the current region." });

            // Partition each entry into at most one themed bucket. Order
            // matters — FT8/FT4 win over generic SSB even though their CAT
            // mode is DATA-U (USB-side data). RTTY beats CW. FM is last.
            var buckets = new Dictionary<string, List<AppMemory>>
            {
                ["FT8"]  = new(),
                ["FT4"]  = new(),
                ["RTTY"] = new(),
                ["CW"]   = new(),
                ["SSB"]  = new(),
                ["FM"]   = new(),
            };
            foreach (var src in bank.Entries)
            {
                // Fresh AppMemory per bucket so the bank store doesn't share
                // references with each other or with MemoryService.
                var e = new AppMemory
                {
                    Label             = src.Label,
                    FrequencyHz       = src.FrequencyHz,
                    Mode              = src.Mode,
                    ClarifierOffsetHz = src.ClarifierOffsetHz,
                    RxClarOn          = src.RxClarOn,
                    TxClarOn          = src.TxClarOn,
                    Antenna           = src.Antenna,
                    IfWidthCode       = src.IfWidthCode,
                    IfShiftHz         = src.IfShiftHz,
                    RoofingCode       = src.RoofingCode,
                    NbOn              = src.NbOn,
                    NbLevel           = src.NbLevel,
                    NrLevel           = src.NrLevel,
                    AgcMode           = src.AgcMode,
                    PowerWatts        = src.PowerWatts,
                    Notes             = src.Notes,
                };

                var label = (e.Label ?? "").ToUpperInvariant();
                var mode  = (e.Mode  ?? "").ToUpperInvariant();
                if (label.Contains("FT8"))            buckets["FT8"].Add(e);
                else if (label.Contains("FT4"))       buckets["FT4"].Add(e);
                else if (mode.StartsWith("RTTY"))     buckets["RTTY"].Add(e);
                else if (mode.StartsWith("CW"))       buckets["CW"].Add(e);
                else if (mode == "FM")                buckets["FM"].Add(e);
                else if (mode == "USB" || mode == "LSB") buckets["SSB"].Add(e);
                // Anything else (e.g. AM beacons) is intentionally dropped —
                // the themes above cover the everyday operating modes.
            }

            var created = new List<string>();
            var skipped = new List<string>();
            var emptyThemes = new List<string>();
            foreach (var (name, entries) in buckets)
            {
                if (entries.Count == 0) { emptyThemes.Add(name); continue; }
                var wasCreated = await _bankService.CreateBankWithEntriesAsync(name, entries, overwrite);
                if (wasCreated) created.Add(name); else skipped.Add(name);
            }

            return Ok(new
            {
                created,
                skipped,
                emptyThemes,
                totalEntries = bank.Entries.Count,
                regionDescription = bank.Description
            });
        }

        private async Task<StarterBankFile?> LoadStarterBankFromDiskAsync()
        {
            var settings = await _settingsService.GetSettingsAsync();
            // Normalise legacy region names. Pages/Index.cshtml.cs does similar mapping.
            var region = settings.BandPlan switch
            {
                "UK"  => "Region1",
                "USA" => "Region2",
                var v => v
            };
            var filename = region switch
            {
                "Region1" => "starter-bank-region1.json",
                "Region2" => "starter-bank-region2.json",
                "Region3" => "starter-bank-region3.json",
                "Japan"   => "starter-bank-japan.json",
                _          => "starter-bank-region1.json"
            };
            var path = Path.Combine(_env.WebRootPath, "data", filename);
            if (!System.IO.File.Exists(path)) return null;
            var json = await System.IO.File.ReadAllTextAsync(path);
            return JsonSerializer.Deserialize<StarterBankFile>(json,
                new JsonSerializerOptions { PropertyNameCaseInsensitive = true });
        }

        // ── ADIF memory import ─────────────────────────────────────────────
        //
        // Read an ADIF file and turn each unique (frequency, mode) pair into
        // a IWC memory. Many operators already have their favourite
        // frequencies in Log4OM or another logger — this saves them retyping.
        //
        // Strategy:
        //  - Parse all QSO records
        //  - Bucket by (frequency-in-Hz, iwc-mode-string) so duplicates from
        //    multiple QSOs on the same frequency collapse into one memory
        //  - Skip entries whose label already exists in the current memory
        //    list (collision-safe; users can re-import without doubling up)
        //  - Default label: "<freq-MHz> <mode>" (e.g. "14.074 DATA-U")
        //
        // No advanced fields are imported — the ADIF format doesn't carry
        // them. AGC / NB / NR etc. stay null so memory recall leaves the
        // radio's current values alone.

        [HttpPost("import-adif")]
        public async Task<IActionResult> ImportAdif(IFormFile? file)
        {
            if (file == null || file.Length == 0)
                return BadRequest(new { error = "No file uploaded." });

            string content;
            try
            {
                using var sr = new StreamReader(file.OpenReadStream());
                content = await sr.ReadToEndAsync();
            }
            catch (Exception ex)
            {
                return BadRequest(new { error = $"Could not read uploaded file: {ex.Message}" });
            }

            var records = AdifParser.Parse(content);
            if (records.Count == 0)
                return BadRequest(new { error = "No ADIF records found in the file." });

            // Deduplicate by (Hz, iwc-mode) so the same frequency appearing in
            // hundreds of QSOs only produces one new memory.
            var seen = new HashSet<(long, string)>();
            var newMemories = new List<AppMemory>();
            int skippedNoFreq = 0;
            foreach (var r in records)
            {
                var hz = AdifParser.FreqMHzToHz(r.Frequency);
                if (!hz.HasValue) { skippedNoFreq++; continue; }
                var mode = AdifParser.AdifModeToRadioMode(r.Mode);
                if (!seen.Add((hz.Value, mode))) continue;
                newMemories.Add(new AppMemory
                {
                    FrequencyHz = hz.Value,
                    Mode        = mode,
                    Label       = $"{(hz.Value / 1e6).ToString("F3", System.Globalization.CultureInfo.InvariantCulture)} {mode}"
                });
            }

            // Skip any whose label collides with an existing memory — keeps
            // repeat imports idempotent. Comparison is case-insensitive and
            // ignores leading/trailing whitespace.
            var existingLabels = new HashSet<string>(
                _memoryService.GetAll().Select(m => (m.Label ?? "").Trim()),
                StringComparer.OrdinalIgnoreCase);
            var toAdd = newMemories.Where(m => !existingLabels.Contains(m.Label.Trim())).ToList();

            if (toAdd.Count == 0)
            {
                return Ok(new
                {
                    parsed     = records.Count,
                    unique     = newMemories.Count,
                    added      = 0,
                    skippedNoFreq,
                    skippedDuplicateLabel = newMemories.Count,
                    message = $"Read {records.Count} record(s); all {newMemories.Count} unique frequency/mode pairs already exist as memories."
                });
            }

            try
            {
                await _memoryService.MergeAsync(toAdd);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Failed to merge ADIF memories");
                return StatusCode(500, new { error = $"Could not save imported memories: {ex.Message}" });
            }

            return Ok(new
            {
                parsed     = records.Count,
                unique     = newMemories.Count,
                added      = toAdd.Count,
                skippedNoFreq,
                skippedDuplicateLabel = newMemories.Count - toAdd.Count,
                message = $"Imported {toAdd.Count} new memor{(toAdd.Count == 1 ? "y" : "ies")} from {records.Count} ADIF record(s)."
            });
        }
    }
}
