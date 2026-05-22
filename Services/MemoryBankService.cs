using System.Text.Json;

namespace Yaesu_Web_Control.Services
{
    public class MemoryBankService
    {
        private static readonly string BanksPath = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
            "MM5AGM", "Yaesu Web Control", "memory-banks.json");

        private static readonly JsonSerializerOptions _opts = new()
        {
            WriteIndented = true,
            PropertyNameCaseInsensitive = true
        };

        private readonly SemaphoreSlim _lock = new(1, 1);
        private readonly MemoryService _memoryService;
        private Dictionary<string, List<AppMemory>> _banks = new();

        public MemoryBankService(MemoryService memoryService)
        {
            _memoryService = memoryService;
            LoadFromDisk();
        }

        private void LoadFromDisk()
        {
            try
            {
                if (!File.Exists(BanksPath)) return;
                var json = File.ReadAllText(BanksPath);
                _banks = JsonSerializer.Deserialize<Dictionary<string, List<AppMemory>>>(json, _opts)
                    ?? new Dictionary<string, List<AppMemory>>();
            }
            catch
            {
                _banks = new Dictionary<string, List<AppMemory>>();
            }
        }

        private void SaveToDisk()
        {
            Directory.CreateDirectory(Path.GetDirectoryName(BanksPath)!);
            File.WriteAllText(BanksPath, JsonSerializer.Serialize(_banks, _opts));
        }

        public IReadOnlyList<string> GetBankNames() =>
            _banks.Keys.OrderBy(k => k, StringComparer.OrdinalIgnoreCase).ToList();

        public async Task SaveBankAsync(string name)
        {
            var memories = _memoryService.GetAll().ToList();
            await _lock.WaitAsync();
            try
            {
                _banks[name] = memories;
                SaveToDisk();
            }
            finally { _lock.Release(); }
        }

        public async Task<bool> LoadBankAsync(string name)
        {
            await _lock.WaitAsync();
            List<AppMemory>? bank;
            try
            {
                if (!_banks.TryGetValue(name, out bank)) return false;
            }
            finally { _lock.Release(); }

            // Clone entries so bank contents are not mutated by MemoryService
            var copies = bank.Select(m => new AppMemory
            {
                Label             = m.Label,
                FrequencyHz       = m.FrequencyHz,
                Mode              = m.Mode,
                ClarifierOffsetHz = m.ClarifierOffsetHz,
                RxClarOn          = m.RxClarOn,
                TxClarOn          = m.TxClarOn
            }).ToList();

            await _memoryService.ReplaceAllAsync(copies);
            return true;
        }

        public async Task<bool> DeleteBankAsync(string name)
        {
            await _lock.WaitAsync();
            try
            {
                if (!_banks.ContainsKey(name)) return false;
                _banks.Remove(name);
                SaveToDisk();
                return true;
            }
            finally { _lock.Release(); }
        }
    }
}
