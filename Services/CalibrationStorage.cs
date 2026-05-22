using System.Text.Json;
using Yaesu_Web_Control.Models.Calibration;
using Microsoft.Extensions.Hosting;

namespace Yaesu_Web_Control.Services;

public class CalibrationStorage
{
    private readonly bool _isDevelopment;
    private readonly string _defaultPath;
    private readonly string _userPath;

    private static readonly JsonSerializerOptions ReadOptions = new()
    {
        PropertyNameCaseInsensitive = true
    };

    private static readonly JsonSerializerOptions WriteOptions = new()
    {
        WriteIndented = true
    };

    public CalibrationStorage(IHostEnvironment hostEnvironment)
    {
        _isDevelopment = hostEnvironment.IsDevelopment();
        _defaultPath = Path.Combine(hostEnvironment.ContentRootPath, "wwwroot", "calibration.default.json");
        _userPath = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
            "MM5AGM",
            "Yaesu Web Control",
            "calibration.user.json");
    }

    public bool IsDevelopmentMode => _isDevelopment;

    public string GetActivePath() => _isDevelopment ? _defaultPath : _userPath;

    public CalibrationFile Load()
    {
        if (_isDevelopment)
        {
            return LoadFromPath(_defaultPath);
        }

        EnsureUserCalibrationExists();
        return LoadFromPath(_userPath);
    }

    public CalibrationFile LoadDefault()
    {
        return LoadFromPath(_defaultPath);
    }

    public void Save(CalibrationFile file)
    {
        var targetPath = GetActivePath();
        var directory = Path.GetDirectoryName(targetPath);

        if (!string.IsNullOrWhiteSpace(directory))
        {
            Directory.CreateDirectory(directory);
        }

        var json = JsonSerializer.Serialize(file, WriteOptions);
        File.WriteAllText(targetPath, json);
    }

    private void EnsureUserCalibrationExists()
    {
        var userDir = Path.GetDirectoryName(_userPath);
        if (!string.IsNullOrWhiteSpace(userDir))
        {
            Directory.CreateDirectory(userDir);
        }

        if (!File.Exists(_userPath))
        {
            // First run — copy default wholesale
            File.Copy(_defaultPath, _userPath, overwrite: false);
            return;
        }

        // File already exists — merge any meters that are in the default but missing from the user file.
        // This handles the case where new meters are added to calibration after the user's first install.
        MergeDefaultsIntoUserFile();
    }

    private void MergeDefaultsIntoUserFile()
    {
        try
        {
            var userFile    = LoadFromPath(_userPath);
            var defaultFile = LoadFromPath(_defaultPath);

            var changed = false;
            foreach (var defaultMeter in defaultFile.Meters)
            {
                if (!userFile.Meters.Any(m => m.Name == defaultMeter.Name))
                {
                    userFile.Meters.Add(defaultMeter);
                    changed = true;
                }
            }

            if (changed)
            {
                Save(userFile);
            }
        }
        catch
        {
            // If the merge fails for any reason, leave the user file as-is.
        }
    }

    private static CalibrationFile LoadFromPath(string path)
    {
        var json = File.ReadAllText(path);
        var file = JsonSerializer.Deserialize<CalibrationFile>(json, ReadOptions) ?? new CalibrationFile();

        foreach (var meter in file.Meters)
        {
            meter.Normalize();
            meter.Points = meter.Points.OrderBy(p => p.Raw).ToList();
        }

        return file;
    }
}
