using Microsoft.AspNetCore.Mvc;
using FTdx101_WebApp.Services;

namespace FTdx101_WebApp.Controllers
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

        public MemoryController(
            MemoryService memoryService,
            ICatClient catClient,
            ISettingsService settingsService,
            RadioStateService radioStateService,
            ILogger<MemoryController> logger)
        {
            _memoryService = memoryService;
            _catClient = catClient;
            _settingsService = settingsService;
            _radioStateService = radioStateService;
            _logger = logger;
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

            // Set frequency
            string freqStr = memory.FrequencyHz.ToString("D9");
            await _catClient.SendCommandAsync($"FA{freqStr};", "MemRecall", CancellationToken.None);
            _radioStateService.FrequencyA = memory.FrequencyHz;

            // Set mode
            if (ModeToCode.TryGetValue(memory.Mode, out char modeCode))
            {
                await _catClient.SendCommandAsync($"MD0{modeCode};", "MemRecall", CancellationToken.None);
                _radioStateService.ModeA = memory.Mode;
            }

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

            return Ok();
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

            var imported = new List<AppMemory>();

            for (int ch = 1; ch <= 99; ch++)
            {
                if (cancellationToken.IsCancellationRequested) break;

                string channel = ch.ToString("D3");

                // Read memory data
                var mrResp = await _catClient.SendCommandAsync($"MR{channel}0;", "MemImport", cancellationToken);
                if (string.IsNullOrWhiteSpace(mrResp) || !mrResp.StartsWith("MR") || mrResp.Length < 27)
                    continue;

                // Parse MR response:
                // MR{ch3}{freq9}{clardir1}{claroffset4}{rxclar1}{txclar1}{mode1}...
                if (!long.TryParse(mrResp.Substring(5, 9), out long freqHz) || freqHz == 0)
                    continue;

                char clarDir = mrResp[14];
                int clarOffset = 0;
                if (int.TryParse(mrResp.Substring(15, 4), out int clarAbs))
                    clarOffset = clarDir == '-' ? -clarAbs : clarAbs;

                bool rxClar = mrResp[19] == '1';
                bool txClar = mrResp[20] == '1';

                string mode = "USB";
                if (mrResp.Length > 21)
                    CodeToMode.TryGetValue(char.ToUpper(mrResp[21]), out mode!);
                mode ??= "USB";

                // Read label via MT (not available on FTDX3000)
                string label = $"CH{channel}";
                if (hasMt)
                {
                    var mtResp = await _catClient.SendCommandAsync($"MT{channel};", "MemImport", cancellationToken);
                    if (!string.IsNullOrWhiteSpace(mtResp) && mtResp.StartsWith("MT") && mtResp.Length >= 40)
                    {
                        // MT response: MT{ch3}{freq9}{clardir1}{claroffset4}{rxclar1}{txclar1}{mode1}{p7}{ctcss1}{tone2}{shift1}{p11}{tag12}
                        // Tag starts at position 28
                        label = mtResp.Substring(28, Math.Min(12, mtResp.Length - 29)).TrimEnd();
                        if (string.IsNullOrWhiteSpace(label)) label = $"CH{channel}";
                    }
                }

                imported.Add(new AppMemory
                {
                    Label            = label,
                    FrequencyHz      = freqHz,
                    Mode             = mode,
                    ClarifierOffsetHz = clarOffset,
                    RxClarOn         = rxClar,
                    TxClarOn         = txClar
                });
            }

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
            bool isFtdx3000 = settings.RadioModel == "FTDX3000";
            bool hasMt = !isFtdx3000;

            var memories = _memoryService.GetAll();
            int written = 0;

            for (int i = 0; i < Math.Min(memories.Count, 99); i++)
            {
                if (cancellationToken.IsCancellationRequested) break;

                var mem = memories[i];
                int channel = i + 1;
                string ch = channel.ToString("D3");
                string freq = mem.FrequencyHz.ToString("D9");
                string clarDir = mem.ClarifierOffsetHz >= 0 ? "+" : "-";
                string clarOff = Math.Abs(mem.ClarifierOffsetHz).ToString("D4");
                int rxBit = mem.RxClarOn ? 1 : 0;
                int txBit = mem.TxClarOn ? 1 : 0;
                char modeCode = ModeToCode.TryGetValue(mem.Mode, out char mc) ? mc : '2';

                // MW{ch3}{freq9}{clardir1}{claroffset4}{rxclar1}{txclar1}{mode1}{0}{ctcss0}{00}{shift0}
                string mwCmd = $"MW{ch}{freq}{clarDir}{clarOff}{rxBit}{txBit}{modeCode}000000;";
                await _catClient.SendCommandAsync(mwCmd, "MemExport", cancellationToken);

                if (hasMt)
                {
                    // MT same as MW plus fixed 0 + 12-char tag (space-padded)
                    string tag = mem.Label.Length > 12 ? mem.Label[..12] : mem.Label.PadRight(12);
                    string mtCmd = $"MT{ch}{freq}{clarDir}{clarOff}{rxBit}{txBit}{modeCode}000000{0}{tag};";
                    await _catClient.SendCommandAsync(mtCmd, "MemExport", cancellationToken);
                }

                written++;
            }

            return Ok(new { written });
        }
    }
}
