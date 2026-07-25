using System.Text.Json;

namespace Icom_Web_Control.Services
{
    public class MemoryService
    {
        public static readonly string MemoriesPath = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
            "MM5AGM", "Icom Web Control", "memories.json");

        private readonly SemaphoreSlim _lock = new(1, 1);
        private List<AppMemory> _memories = new();
        private int _nextId = 1;

        public MemoryService()
        {
            LoadFromDisk();
        }

        /// <summary>Re-read memories.json from disk. Used after an external
        /// import (e.g. the unified backup/restore) has overwritten the file.</summary>
        public void ReloadFromDisk() => LoadFromDisk();

        private void LoadFromDisk()
        {
            try
            {
                if (!File.Exists(MemoriesPath))
                {
                    _memories = new List<AppMemory>();
                    return;
                }
                var json = File.ReadAllText(MemoriesPath);
                _memories = JsonSerializer.Deserialize<List<AppMemory>>(json,
                    new JsonSerializerOptions { PropertyNameCaseInsensitive = true })
                    ?? new List<AppMemory>();
                _nextId = _memories.Count > 0 ? _memories.Max(m => m.Id) + 1 : 1;
            }
            catch
            {
                _memories = new List<AppMemory>();
                _nextId = 1;
            }
        }

        private void SaveToDisk()
        {
            Directory.CreateDirectory(Path.GetDirectoryName(MemoriesPath)!);
            File.WriteAllText(MemoriesPath,
                JsonSerializer.Serialize(_memories, new JsonSerializerOptions { WriteIndented = true }));
        }

        public IReadOnlyList<AppMemory> GetAll() =>
            _memories.OrderBy(m => m.SortOrder).ThenBy(m => m.Id).ToList();

        public AppMemory? GetById(int id) => _memories.FirstOrDefault(m => m.Id == id);

        public async Task<AppMemory> AddAsync(AppMemory memory)
        {
            await _lock.WaitAsync();
            try
            {
                memory.Id = _nextId++;
                if (memory.SortOrder == 0) memory.SortOrder = _memories.Count + 1;
                _memories.Add(memory);
                SaveToDisk();
                return memory;
            }
            finally { _lock.Release(); }
        }

        public async Task<bool> UpdateAsync(AppMemory memory)
        {
            await _lock.WaitAsync();
            try
            {
                var idx = _memories.FindIndex(m => m.Id == memory.Id);
                if (idx < 0) return false;
                _memories[idx] = memory;
                SaveToDisk();
                return true;
            }
            finally { _lock.Release(); }
        }

        public async Task<bool> DeleteAsync(int id)
        {
            await _lock.WaitAsync();
            try
            {
                var existing = _memories.FirstOrDefault(m => m.Id == id);
                if (existing == null) return false;
                _memories.Remove(existing);
                SaveToDisk();
                return true;
            }
            finally { _lock.Release(); }
        }

        public async Task ReplaceAllAsync(List<AppMemory> memories)
        {
            await _lock.WaitAsync();
            try
            {
                for (int i = 0; i < memories.Count; i++)
                {
                    memories[i].Id = i + 1;
                    memories[i].SortOrder = i + 1;
                }
                _memories = memories;
                _nextId = memories.Count > 0 ? memories.Max(m => m.Id) + 1 : 1;
                SaveToDisk();
            }
            finally { _lock.Release(); }
        }

        public async Task MergeAsync(List<AppMemory> imported)
        {
            await _lock.WaitAsync();
            try
            {
                foreach (var m in imported)
                {
                    m.Id = _nextId++;
                    m.SortOrder = _memories.Count + 1;
                    _memories.Add(m);
                }
                SaveToDisk();
            }
            finally { _lock.Release(); }
        }
    }
}
