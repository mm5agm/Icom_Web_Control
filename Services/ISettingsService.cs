using Yaesu_Web_Control.Models;

namespace Yaesu_Web_Control.Services
{
    public interface ISettingsService
    {
        Task<ApplicationSettings> GetSettingsAsync();
        Task SaveSettingsAsync(ApplicationSettings settings);
    }
}