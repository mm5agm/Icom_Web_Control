namespace Yaesu_Web_Control.Models
{
    public class ApplicationSettings
    {
        // Connection Settings
        public string SerialPort { get; set; } = "COM3";
        public int BaudRate { get; set; } = 38400;
        public string WebAddress { get; set; } = "0.0.0.0"; // Bind to all interfaces
        public string RadioModel { get; set; } = "FTdx101MP"; // MP = dual receiver, D = single receiver


        // External Applications - Command Lines
        public string WsjtxCommandLine { get; set; } = @"C:\WSJT\wsjtx\bin\wsjtx.exe --rig-name=WebApp";
        public string JtalertCommandLine { get; set; } = @"C:\HamApps\JTAlert\JTAlert.exe";
        public string Log4omCommandLine { get; set; } = @"C:\Program Files (x86)\Log4OM 2\Log4OM.exe";

        // External Applications - Custom Names (user can rename buttons)
        public string App1Name { get; set; } = "WSJT-X";
        public string App2Name { get; set; } = "JTAlert";
        public string App3Name { get; set; } = "Log4OM";

        // External Applications - Show/Hide buttons (optional apps)
        public bool ShowWsjtxButton { get; set; } = true;
        public bool ShowJtalertButton { get; set; } = true;
        public bool ShowLog4omButton { get; set; } = true;

        // WSJT-X UDP Settings
        // Default: Use the same multicast address as configured in WSJT-X
        // Common values: 224.0.0.1, 239.255.0.1, or 127.0.0.1 for unicast
        public string WsjtxUdpAddress { get; set; } = "239.255.0.1";
        public int WsjtxUdpPort { get; set; } = 2237;

        // Last Radio State (persisted between sessions)
        public RadioState LastRadioState { get; set; } = new();

        // Band Plan
        public string BandPlan { get; set; } = "Region1";

        // SDR Spectrum Display
        // SdrDeviceKey: the SoapySDR args string identifying the device (e.g. "driver=rtlsdr,serial=00000001").
        // Empty string means no SDR configured; the spectrum display is hidden.
        public string SdrDeviceKey { get; set; } = string.Empty;
        public double SdrSampleRateHz { get; set; } = 2_048_000;
        public long SdrIfFrequencyHz { get; set; } = 9_000_000;
        public int SdrFftSize { get; set; } = 1024;

        // CW keyer message memories M1-M5 (sent via KY command)
        public List<string> CwMessages { get; set; } = new() { "CQ CQ DE {CALL}", "TU 73", "QRZ?", "UR 5NN", "DE {CALL}" };

        // Optional roofing filters installed in the radio (FTdx101MP/D only).
        // "6"=12kHz and "7"=3kHz are always fitted. "8"=1.2kHz, "9"=600Hz, "A"=300Hz are optional.
        // FTdx10 has fixed roofing filters and ignores this setting.
        public List<string> InstalledRoofingFilters { get; set; } = new() { "6", "7", "8", "9", "A" };
    }

    public class RadioState
    {
        // VFO-A State
        public long FrequencyA { get; set; } = 14074000; // Default: 14.074 MHz (FT8)
        public string ModeA { get; set; } = "USB";
        public string AntennaA { get; set; } = "1";

        // VFO-B State
        public long FrequencyB { get; set; } = 14074000; // Default: 14.074 MHz (FT8)
        public string ModeB { get; set; } = "USB";
        public string AntennaB { get; set; } = "1";

        // IF Width/Shift
        public string IfWidthA { get; set; } = "8";
        public string IfWidthB { get; set; } = "8";
        public int IfShiftA { get; set; } = 0;
        public int IfShiftB { get; set; } = 0;
    }
}