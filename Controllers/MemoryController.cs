using Microsoft.AspNetCore.Mvc;
using System.Text.Json;
using System.Text.Json.Serialization;
using Yaesu_Web_Control.Services;

namespace Yaesu_Web_Control.Controllers
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
        private readonly ICatClient _catClient;
        private readonly ISettingsService _settingsService;
        private readonly RadioStateService _radioStateService;
        private readonly ILogger<MemoryController> _logger;
        private readonly IWebHostEnvironment _env;

        public MemoryController(
            MemoryService memoryService,
            ICatClient catClient,
            ISettingsService settingsService,
            RadioStateService radioStateService,
            ILogger<MemoryController> logger,
            IWebHostEnvironment env)
        {
            _memoryService = memoryService;
            _catClient = catClient;
            _settingsService = settingsService;
            _radioStateService = radioStateService;
            _logger = logger;
            _env = env;
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

        // ── Recall (tune VFO A to a memory) ─────────────────────────────────

        [HttpPost("{id:int}/recall")]
        public async Task<IActionResult> Recall(int id)
        {
            var memory = _memoryService.GetById(id);
            if (memory == null) return NotFound();

            var settings = await _settingsService.GetSettingsAsync();
            bool useCf = settings.RadioModel is "FTdx10" or "FT-710";

            // Set mode before frequency so the radio applies any pitch/carrier offset
            // (e.g. CW sidetone offset) before the VFO is tuned — prevents ~700 Hz landing error.
            if (ModeToCode.TryGetValue(memory.Mode, out char modeCode))
            {
                await _catClient.SendCommandAsync($"MD0{modeCode};", "MemRecall", CancellationToken.None);
                _radioStateService.ModeA = memory.Mode;
                await Task.Delay(50);
            }

            // Set frequency
            string freqStr = memory.FrequencyHz.ToString("D9");
            await _catClient.SendCommandAsync($"FA{freqStr};", "MemRecall", CancellationToken.None);
            _radioStateService.FrequencyA = memory.FrequencyHz;

            // Set clarifier
            if (useCf)
            {
                int rxBit = memory.RxClarOn ? 1 : 0;
                int txBit = memory.TxClarOn ? 1 : 0;
                await _catClient.SendCommandAsync($"CF001{rxBit}{txBit}000;", "MemRecall", CancellationToken.None);
                string sign = memory.ClarifierOffsetHz >= 0 ? "+" : "-";
                await _catClient.SendCommandAsync($"CF001{sign}{Math.Abs(memory.ClarifierOffsetHz):D4};", "MemRecall", CancellationToken.None);
            }
            else
            {
                await _catClient.SendCommandAsync($"RT{(memory.RxClarOn ? 1 : 0)};", "MemRecall", CancellationToken.None);
                await _catClient.SendCommandAsync($"XT{(memory.TxClarOn ? 1 : 0)};", "MemRecall", CancellationToken.None);
                await _catClient.SendCommandAsync("RC;", "MemRecall", CancellationToken.None);
                if (memory.ClarifierOffsetHz > 0)
                    await _catClient.SendCommandAsync($"RU{memory.ClarifierOffsetHz:D4};", "MemRecall", CancellationToken.None);
                else if (memory.ClarifierOffsetHz < 0)
                    await _catClient.SendCommandAsync($"RD{Math.Abs(memory.ClarifierOffsetHz):D4};", "MemRecall", CancellationToken.None);
            }

            _radioStateService.ClarifierOffsetA = memory.ClarifierOffsetHz;
            _radioStateService.RxClarOn = memory.RxClarOn;
            _radioStateService.TxClarOn = memory.TxClarOn;

            // ── Apply advanced / optional fields ───────────────────────────────
            // Each field is applied only if non-null. Null = leave radio alone.
            if (!string.IsNullOrEmpty(memory.Antenna))
            {
                await _catClient.SendCommandAsync($"AN0{memory.Antenna};", "MemRecall", CancellationToken.None);
                _radioStateService.AntennaA = memory.Antenna;
            }
            if (!string.IsNullOrEmpty(memory.IfWidthCode) && int.TryParse(memory.IfWidthCode, out int ifw))
            {
                await _catClient.SendCommandAsync($"SH00{ifw:D2};", "MemRecall", CancellationToken.None);
                _radioStateService.IfWidthA = memory.IfWidthCode;
            }
            if (memory.IfShiftHz.HasValue)
            {
                int shift = memory.IfShiftHz.Value;
                char sign = shift >= 0 ? '+' : '-';
                await _catClient.SendCommandAsync($"IS00{sign}{Math.Abs(shift):D4};", "MemRecall", CancellationToken.None);
                _radioStateService.IfShiftA = shift;
            }
            if (!string.IsNullOrEmpty(memory.RoofingCode))
            {
                // FTdx10/FT-710 have no CAT roofing filter control; skip silently.
                if (settings.RadioModel is not ("FTdx10" or "FT-710"))
                {
                    await _catClient.SendCommandAsync($"RF0{memory.RoofingCode};", "MemRecall", CancellationToken.None);
                    _radioStateService.RoofingFilterA = memory.RoofingCode;
                }
            }
            if (memory.NbOn.HasValue)
            {
                await _catClient.SendCommandAsync($"NB0{(memory.NbOn.Value ? 1 : 0)};", "MemRecall", CancellationToken.None);
                _radioStateService.NbA = memory.NbOn.Value ? "1" : "0";
            }
            if (memory.NbLevel.HasValue)
            {
                int nbl = Math.Clamp(memory.NbLevel.Value, 1, 20);
                await _catClient.SendCommandAsync($"NL0{nbl:D3};", "MemRecall", CancellationToken.None);
                _radioStateService.NbLevelA = nbl;
            }
            if (!string.IsNullOrEmpty(memory.NrLevel))
            {
                await _catClient.SendCommandAsync($"NR0{memory.NrLevel};", "MemRecall", CancellationToken.None);
                _radioStateService.NrA = memory.NrLevel;
            }
            if (!string.IsNullOrEmpty(memory.AgcMode))
            {
                await _catClient.SendCommandAsync($"GT0{memory.AgcMode};", "MemRecall", CancellationToken.None);
                _radioStateService.AgcA = memory.AgcMode;
            }
            if (memory.PowerWatts.HasValue)
            {
                int pw = Math.Clamp(memory.PowerWatts.Value, 5, 200);
                await _catClient.SendCommandAsync($"PC{pw:D3};", "MemRecall", CancellationToken.None);
                _radioStateService.Power = pw;
            }
            // Notes are app-side only; not sent to the radio.

            return Ok();
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

        [HttpPost("import-radio")]
        [RequestSizeLimit(1_000_000)]
        public async Task<IActionResult> ImportFromRadio(
            [FromBody] ImportRequest request,
            CancellationToken cancellationToken)
        {
            var settings = await _settingsService.GetSettingsAsync();
            bool isFtdx3000 = settings.RadioModel == "FTDX3000";
            bool hasMt = !isFtdx3000;

            // Disable Auto-Information so responses are direct query replies,
            // not unsolicited AI updates that would bypass _pendingResponses.
            await _catClient.SendCommandAsync("AI0;", "MemImport", cancellationToken);
            await Task.Delay(50, cancellationToken);

            var imported = new List<AppMemory>();

            try
            {
                for (int ch = 1; ch <= 99; ch++)
                {
                    if (cancellationToken.IsCancellationRequested) break;

                    string channel = ch.ToString("D3");

                    // MR{ch}; queries memory data without recalling to VFO.
                    // Format: MR{ch3}{freq9}{clardir1}{claroff4}{rx1}{tx1}{mode1}... (27 chars)
                    // Note: MR{ch}0; is the RECALL command (no response); MR{ch}; is the READ command.
                    var mrResp = await _catClient.SendCommandAsync($"MR{channel};", "MemImport", cancellationToken, timeoutMs: 500);
                    if (string.IsNullOrWhiteSpace(mrResp) || !mrResp.StartsWith("MR") || mrResp.Length < 22)
                        continue;

                    if (!long.TryParse(mrResp.Substring(5, 9), out long freqHz) || freqHz == 0)
                        continue;

                    char clarDir = mrResp[14];
                    int clarOffset = 0;
                    if (int.TryParse(mrResp.Substring(15, 4), out int clarAbs))
                        clarOffset = clarDir == '-' ? -clarAbs : clarAbs;

                    bool rxClar = mrResp[19] == '1';
                    bool txClar = mrResp[20] == '1';

                    string mode = "USB";
                    CodeToMode.TryGetValue(char.ToUpper(mrResp[21]), out mode!);
                    mode ??= "USB";

                    // Read label via MT{ch}; — only available on non-FTDX3000 radios.
                    // The label sits at position 28 of the 40-char MT response.
                    string label = $"CH{channel}";
                    if (hasMt)
                    {
                        var mtResp = await _catClient.SendCommandAsync($"MT{channel};", "MemImport", cancellationToken, timeoutMs: 500);
                        if (!string.IsNullOrWhiteSpace(mtResp) && mtResp.Length >= 40)
                        {
                            label = mtResp.Substring(28, Math.Min(12, mtResp.Length - 29)).TrimEnd();
                            if (string.IsNullOrWhiteSpace(label)) label = $"CH{channel}";
                        }
                    }

                    imported.Add(new AppMemory
                    {
                        Label             = label,
                        FrequencyHz       = freqHz,
                        Mode              = mode,
                        ClarifierOffsetHz = clarOffset,
                        RxClarOn          = rxClar,
                        TxClarOn          = txClar
                    });
                }
            }
            finally
            {
                // Always re-enable Auto-Information, even if import was cancelled or failed
                await _catClient.SendCommandAsync("AI1;", "MemImport", CancellationToken.None);
            }

            if (imported.Count == 0)
                return Ok(new { imported = 0, mode = request.Mode, warning = "No channels found on radio — existing app memories were not changed." });

            if (request.Mode == "replace")
                await _memoryService.ReplaceAllAsync(imported);
            else
                await _memoryService.MergeAsync(imported);

            return Ok(new { imported = imported.Count, mode = request.Mode });
        }

        // ── Export to Radio ──────────────────────────────────────────────────

        [HttpPost("export-radio")]
        public async Task<IActionResult> ExportToRadio(CancellationToken cancellationToken)
        {
            var settings = await _settingsService.GetSettingsAsync();
            bool hasMt = settings.RadioModel != "FTDX3000";
            var memories = _memoryService.GetAll();
            int written = 0;
            for (int i = 0; i < Math.Min(memories.Count, 99); i++)
            {
                if (cancellationToken.IsCancellationRequested) break;
                await WriteMemoryToChannel(memories[i], i + 1, hasMt, cancellationToken);
                written++;
            }
            return Ok(new { written });
        }

        [HttpPost("export-radio-add")]
        public async Task<IActionResult> ExportToRadioAdd(CancellationToken cancellationToken)
        {
            var settings = await _settingsService.GetSettingsAsync();
            bool hasMt = settings.RadioModel != "FTDX3000";

            // Scan rig to find empty channels
            var emptyChannels = new List<int>();
            for (int ch = 1; ch <= 99; ch++)
            {
                if (cancellationToken.IsCancellationRequested)
                    return Ok(new { written = 0, noRoom = 0, cancelled = true });

                var mrResp = await _catClient.SendCommandAsync($"MR{ch:D3}0;", "MemExportAdd", cancellationToken);
                bool isEmpty = string.IsNullOrWhiteSpace(mrResp)
                    || mrResp.Length < 14
                    || !long.TryParse(mrResp.Substring(5, 9), out long f)
                    || f == 0;
                if (isEmpty) emptyChannels.Add(ch);
            }

            var memories = _memoryService.GetAll();
            int written = 0, noRoom = 0;

            for (int i = 0; i < memories.Count; i++)
            {
                if (cancellationToken.IsCancellationRequested) break;
                if (i >= emptyChannels.Count) { noRoom++; continue; }
                await WriteMemoryToChannel(memories[i], emptyChannels[i], hasMt, cancellationToken);
                written++;
            }

            return Ok(new { written, noRoom, totalEmpty = emptyChannels.Count });
        }

        private async Task WriteMemoryToChannel(
            AppMemory mem, int channel, bool hasMt, CancellationToken cancellationToken)
        {
            string ch      = channel.ToString("D3");
            string freq    = mem.FrequencyHz.ToString("D9");
            string clarDir = mem.ClarifierOffsetHz >= 0 ? "+" : "-";
            string clarOff = Math.Abs(mem.ClarifierOffsetHz).ToString("D4");
            int rxBit      = mem.RxClarOn ? 1 : 0;
            int txBit      = mem.TxClarOn ? 1 : 0;
            char modeCode  = ModeToCode.TryGetValue(mem.Mode, out char mc) ? mc : '2';

            await _catClient.SendCommandAsync(
                $"MW{ch}{freq}{clarDir}{clarOff}{rxBit}{txBit}{modeCode}000000;", "MemExport", cancellationToken);

            if (hasMt)
            {
                string tag = mem.Label.Length > 12 ? mem.Label[..12] : mem.Label.PadRight(12);
                await _catClient.SendCommandAsync(
                    $"MT{ch}{freq}{clarDir}{clarOff}{rxBit}{txBit}{modeCode}000000{0}{tag};", "MemExport", cancellationToken);
            }
        }

        // ── YWC starter bank (bundled with the app) ──────────────────────────
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
        // a YWC memory. Many operators already have their favourite
        // frequencies in Log4OM or another logger — this saves them retyping.
        //
        // Strategy:
        //  - Parse all QSO records
        //  - Bucket by (frequency-in-Hz, ywc-mode-string) so duplicates from
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

            // Deduplicate by (Hz, ywc-mode) so the same frequency appearing in
            // hundreds of QSOs only produces one new memory.
            var seen = new HashSet<(long, string)>();
            var newMemories = new List<AppMemory>();
            int skippedNoFreq = 0;
            foreach (var r in records)
            {
                var hz = AdifParser.FreqMHzToHz(r.Frequency);
                if (!hz.HasValue) { skippedNoFreq++; continue; }
                var mode = AdifParser.AdifModeToYwc(r.Mode);
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
