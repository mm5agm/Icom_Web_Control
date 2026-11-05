using System.Runtime.CompilerServices;

namespace IcomWebControl.Tests;

/// <summary>
/// The exact bytes each IC-7300 is sent, for every action on IRadioController.
/// These files are what keeps work for another radio from changing what today's
/// users send. If a change to those bytes is intended, run the tests with
/// IWC_UPDATE_GOLDEN=1 and review the diff of the file before committing it.
/// </summary>
public class GoldenFrameTests
{
    [Theory]
    [InlineData((byte)0xB6, "golden-frames-ic7300mk2.txt")]
    [InlineData((byte)0x94, "golden-frames-ic7300.txt")]
    public async Task AnIc7300IsSentExactlyTheGoldenBytes(byte address, string file)
    {
        var actual = RadioActionRecorder.Render(await RadioActionRecorder.RecordAll(address));

        if (Environment.GetEnvironmentVariable("IWC_UPDATE_GOLDEN") == "1")
        {
            await File.WriteAllTextAsync(Path.Combine(SourceDir(), "Fixtures", file), actual);
            return;
        }

        var expected = (await File.ReadAllTextAsync(CivRecording.FixturePath(file))).ReplaceLineEndings("\n");
        Assert.Equal(expected, actual);
    }

    private static string SourceDir([CallerFilePath] string path = "") => Path.GetDirectoryName(path)!;
}
