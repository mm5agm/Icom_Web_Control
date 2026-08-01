using Icom_Web_Control.Models;

namespace Icom_Web_Control.Services
{
    public interface ISettingsService
    {
        Task<ApplicationSettings> GetSettingsAsync();
        Task SaveSettingsAsync(ApplicationSettings settings);

        /// <summary>Absolute path to the user settings file on disk.</summary>
        string GetSettingsFilePath();

        /// <summary>Drop the in-memory cache so the next GetSettingsAsync re-reads from disk.
        /// Used after an import overwrites the file externally.</summary>
        void InvalidateCache();
    }
}