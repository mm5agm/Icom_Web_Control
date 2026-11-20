using System;
using System.IO;
using Xunit;

namespace IcomWebControl.Tests
{
    /// <summary>
    /// A /dev/serial/by-id path is never in SerialPort.GetPortNames(), so
    /// checking the CI-V port against that list alone turned it away as "not
    /// present", on a Linux host and in Docker (found in YWC, 2026-11-20).
    /// </summary>
    public class LinuxByIdPortsTests
    {
        [Theory]
        [InlineData("Services/Civ/CivBusService.cs")]
        [InlineData("Services/CivRadioController.cs")]
        [InlineData("Program.cs")]
        public void PresenceChecksAllowByIdPaths(string relative)
        {
            string src = File.ReadAllText(Locate(relative));
            Assert.Contains("SerialPortPaths.IsPresent(", src);
            Assert.DoesNotContain("Array.IndexOf(available, port", src);
        }

        private static string Locate(string relative)
        {
            string native = relative.Replace('/', Path.DirectorySeparatorChar);
            for (var dir = new DirectoryInfo(AppContext.BaseDirectory); dir is not null; dir = dir.Parent)
            {
                string candidate = Path.Combine(dir.FullName, native);
                if (File.Exists(candidate) && !candidate.Contains($"{Path.DirectorySeparatorChar}bin{Path.DirectorySeparatorChar}")) return candidate;
            }
            throw new FileNotFoundException(relative);
        }
    }
}
