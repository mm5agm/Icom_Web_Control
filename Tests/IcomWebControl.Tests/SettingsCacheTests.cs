using System.Text.Json;
using Icom_Web_Control.Models;
using Icom_Web_Control.Services;
using Microsoft.Extensions.Logging.Abstractions;

namespace IcomWebControl.Tests;

// D#51 (Paul VK2AHB, pre26): GetSettingsAsync read the file from disk on every
// call, one caller at a time, and on a PC at 100% CPU the reads queued up at
// startup. It now reads the file only when it changes.
public sealed class SettingsCacheTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "iwc-settings-" + Guid.NewGuid().ToString("N"));
    private readonly string _path;

    public SettingsCacheTests()
    {
        Directory.CreateDirectory(_dir);
        _path = Path.Combine(_dir, "appsettings.user.json");
    }

    public void Dispose()
    {
        try { Directory.Delete(_dir, recursive: true); } catch (IOException) { }
    }

    private SettingsService NewService() => new(_path, NullLogger<SettingsService>.Instance);

    private void WriteFile(string port) =>
        File.WriteAllText(_path, JsonSerializer.Serialize(new ApplicationSettings { SerialPort = port }));

    [Fact]
    public async Task Unchanged_file_is_served_from_memory()
    {
        WriteFile("COM8");
        var s = NewService();
        Assert.Equal("COM8", (await s.GetSettingsAsync()).SerialPort);

        // Hold the file open with no sharing: a read from disk would throw
        // and return defaults. From memory it still answers COM8.
        using (new FileStream(_path, FileMode.Open, FileAccess.Read, FileShare.None))
        {
            var task = s.GetSettingsAsync();
            Assert.True(task.IsCompletedSuccessfully);   // no waiting: callers that block on it don't
            Assert.Equal("COM8", task.Result.SerialPort);
        }
    }

    [Fact]
    public async Task Each_caller_gets_its_own_copy()
    {
        WriteFile("COM8");
        var s = NewService();
        var a = await s.GetSettingsAsync();
        a.SerialPort = "COM99";   // changed but not saved
        Assert.Equal("COM8", (await s.GetSettingsAsync()).SerialPort);
    }

    [Fact]
    public async Task A_save_is_seen_at_once()
    {
        WriteFile("COM8");
        var s = NewService();
        var a = await s.GetSettingsAsync();
        a.SerialPort = "COM5";
        await s.SaveSettingsAsync(a);
        Assert.Equal("COM5", (await s.GetSettingsAsync()).SerialPort);
        Assert.Equal("COM5", (await NewService().GetSettingsAsync()).SerialPort);
    }

    [Fact]
    public async Task A_change_to_the_file_is_read_again()
    {
        WriteFile("COM8");
        var s = NewService();
        Assert.Equal("COM8", (await s.GetSettingsAsync()).SerialPort);

        // A backup restore, or an edit outside IWC.
        WriteFile("COM10");
        File.SetLastWriteTimeUtc(_path, DateTime.UtcNow.AddMinutes(1));
        Assert.Equal("COM10", (await s.GetSettingsAsync()).SerialPort);
    }

    [Fact]
    public async Task InvalidateCache_reads_the_file_again()
    {
        WriteFile("COM8");
        var s = NewService();
        await s.GetSettingsAsync();
        var stamp = File.GetLastWriteTimeUtc(_path);
        WriteFile("COM9");   // same length as COM8
        File.SetLastWriteTimeUtc(_path, stamp);   // same time and length: only the invalidate tells it
        s.InvalidateCache();
        Assert.Equal("COM9", (await s.GetSettingsAsync()).SerialPort);
    }

    [Fact]
    public async Task No_file_gives_defaults_until_one_appears()
    {
        var s = NewService();
        Assert.Equal(new ApplicationSettings().SerialPort, (await s.GetSettingsAsync()).SerialPort);
        WriteFile("COM12");
        Assert.Equal("COM12", (await s.GetSettingsAsync()).SerialPort);
    }
}
