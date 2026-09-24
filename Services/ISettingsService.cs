using Icom_Web_Control.Models;

namespace Icom_Web_Control.Services
{
    public interface ISettingsService
    {
        Task<ApplicationSettings> GetSettingsAsync();
        Task SaveSettingsAsync(ApplicationSettings settings);

        /// <summary>In-memory snapshot, never touching disk. Empty defaults until the
        /// first GetSettingsAsync has loaded the file. For callers on threads that
        /// must not block on IO (the Radio Display capture loop).</summary>
        ApplicationSettings GetCachedSettings();

        /// <summary>Absolute path to the user settings file on disk.</summary>
        string GetSettingsFilePath();

        /// <summary>Drop the in-memory cache so the next GetSettingsAsync re-reads from disk.
        /// Used after an import overwrites the file externally.</summary>
        void InvalidateCache();
    }
}