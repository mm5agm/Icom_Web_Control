namespace Icom_Web_Control.Services.RadioMenu;

/// <summary>
/// The IC-7300MK2's SET menu, as CI-V 1A 05 items 00 01–02 70. Transcribed
/// from the command table in docs/manuals/IC-7300MK2_ENG_CI-V_0.pdf
/// (pp. 7–14), with the data formats from pp. 18 and 20–22.
///
/// Where the manual's layout ran two columns together (01 01–01 07,
/// 01 24–01 31, 01 37–01 51) the order was taken from the data column and
/// the cross-references it gives (01 28 = My Call on p. 18, 01 31 = System
/// Language from 02 27's note). The p. 22 UTC offset box says 01 34, but the
/// table puts the NTP switch there and the UTC offset at 01 36; the table is
/// followed. [▲]/[▼] for 00 59/00 60 are from the Advanced manual, whose
/// defaults (M-CH UP / M-CH DOWN) match. Read back from the radio on
/// 2026-10-25: 268 of 270 answered; 01 30 (Display Language) and 02 27
/// (Japanese Morse Decode) did not, the latter needing a Japanese system
/// language per the manual.
/// Unknown values fall back to the data bytes in hex.
/// </summary>
public static class Ic7300Mk2Menu
{
    public static readonly IReadOnlyList<RadioMenuItem> Items = Build();

    private static List<RadioMenuItem> Build()
    {
        var items = new List<RadioMenuItem>();
        string section = "", group = "";
        void Section(string name) => section = name;
        void Group(string name) => group = name;
        void Item(int number, string function, RadioMenuCodec codec, bool tx = false, string? fix = null)
            => items.Add(new RadioMenuItem(number, section, group, function, codec, tx, fix));

        // Items Load and the editor leave alone, and why.
        const string link = "This sets the CI-V or network link itself, so IWC leaves it alone. Change it on the radio.";
        const string clock = "The clock is not a setting, so IWC leaves it alone. Set it on the radio or with NTP.";

        var onOff = Choice("OFF", "ON");
        var hpfLpf = HpfLpf();
        var tone = Tone();
        var percent = Percent();
        var rgb = Rgb();
        var ip = Ip();
        string[] txDelay = { "OFF", "10 ms", "15 ms", "20 ms", "25 ms", "30 ms" };
        string[] outputSelect = { "AF", "IF" };
        string[] afSql = { "OFF (OPEN)", "ON" };
        string[] modSource = { "MIC", "USB", "ACC", "MIC,USB", "MIC,ACC", "LAN" };
        string[] usbLine = { "OFF", "USB (A) DTR", "USB (A) RTS", "USB (B) DTR", "USB (B) RTS" };
        string[] notch = { "Auto", "Manual", "Auto/Manual" };
        string[] memories = { "Voice/Keyer/RTTY Memory 1", "Voice/Keyer/RTTY Memory 2", "Voice/Keyer/RTTY Memory 3", "Voice/Keyer/RTTY Memory 4" };
        string[] micKeys =
        {
            "---", "UP", "DOWN", "UP (VFO: kHz)", "DOWN (VFO: kHz)", "XFC", "VFO/MEMO",
            "BAND UP", "BAND DOWN", "SPEECH", "MODE",
            memories[0], memories[1], memories[2], memories[3], "TS", "MPAD", "SPLIT", "A/B", "TUNER",
        };

        Section("SET");
        Group("Tone Control/TBW > RX > SSB");
        Item(1, "HPF/LPF", hpfLpf);
        Item(2, "Bass", tone);
        Item(3, "Treble", tone);
        Group("Tone Control/TBW > RX > AM");
        Item(4, "HPF/LPF", hpfLpf);
        Item(5, "Bass", tone);
        Item(6, "Treble", tone);
        Group("Tone Control/TBW > RX > FM");
        Item(7, "HPF/LPF", hpfLpf);
        Item(8, "Bass", tone);
        Item(9, "Treble", tone);
        Group("Tone Control/TBW > RX > CW");
        Item(10, "HPF/LPF", hpfLpf);
        Group("Tone Control/TBW > RX > RTTY");
        Item(11, "HPF/LPF", hpfLpf);
        Group("Tone Control/TBW > TX > SSB");
        Item(12, "Bass", tone, tx: true);
        Item(13, "Treble", tone, tx: true);
        Item(14, "TBW (WIDE)", Tbw(), tx: true);
        Item(15, "TBW (MID)", Tbw(), tx: true);
        Item(16, "TBW (NAR)", Tbw(), tx: true);
        Group("Tone Control/TBW > TX > SSB-D");
        Item(17, "TBW", Tbw(), tx: true);
        Group("Tone Control/TBW > TX > AM");
        Item(18, "Bass", tone, tx: true);
        Item(19, "Treble", tone, tx: true);
        Group("Tone Control/TBW > TX > FM");
        Item(20, "Bass", tone, tx: true);
        Item(21, "Treble", tone, tx: true);

        Group("Function");
        Item(22, "Beep Level", percent);
        Item(23, "Beep Level Limit", onOff);
        Item(24, "Beep (Confirmation)", onOff);
        Item(25, "Band Edge Beep", Choice("OFF", "ON (Default)", "ON (User)", "ON (User) & TX Limit"), tx: true);
        Item(26, "RF/SQL Control", Choice("Auto", "SQL", "RF+SQL"));
        Item(27, "Cancel CI-V Remote Set Levels", Choice("All Volume Levels", "Operated Volume Level"));
        Item(28, "MF Band ATT", onOff);
        Group("Function > TX Delay");
        Item(29, "HF", Choice(txDelay), tx: true);
        Item(30, "50M", Choice(txDelay), tx: true);
        Item(31, "70M", Choice(txDelay), tx: true);
        Group("Function");
        Item(32, "Time-Out Timer (CI-V)", Choice("OFF", "3 min", "5 min", "10 min", "20 min", "30 min"), tx: true);
        Group("Function > SPLIT");
        Item(33, "Quick SPLIT", onOff);
        Item(34, "FM SPLIT Offset (HF)", SplitOffset(), tx: true);
        Item(35, "FM SPLIT Offset (50M)", SplitOffset(), tx: true);
        Item(36, "SPLIT LOCK", onOff);
        Group("Function > Tuner");
        Item(37, "[TUNER] Switch", Choice("Manual", "Auto"), tx: true);
        Item(38, "PTT Start", onOff, tx: true);
        Group("Function");
        Item(39, "RTTY Mark Frequency", Choice("1275 Hz", "1615 Hz", "2125 Hz"));
        Item(40, "RTTY Shift Width", Choice("170 Hz", "200 Hz", "425 Hz"));
        Item(41, "RTTY Keying Polarity", Choice("Normal", "Reverse"), tx: true);
        Group("Function > SPEECH");
        Item(42, "SPEECH Language", Choice("English", "Japanese"));
        Item(43, "SPEECH Speed", Choice("Slow", "Fast"));
        Item(44, "S-Level SPEECH", onOff);
        Item(45, "MODE SPEECH", onOff);
        Item(46, "SPEECH Level", percent);
        Group("Function");
        Item(47, "[SPEECH/LOCK] Switch", Choice("SPEECH/LOCK", "LOCK/SPEECH"));
        Item(48, "Lock Function", Choice("MAIN DIAL", "PANEL"));
        Item(49, "Memo Pad Quantity", Choice("5 ch", "10 ch"));
        Item(50, "MAIN DIAL Auto TS", Choice("OFF", "Low", "High"));
        Item(51, "MIC Up/Down Speed", Choice("Slow", "Fast"));
        Item(52, "Quick RIT/⊿TX Clear", onOff);
        Item(53, "[NOTCH] Switch (SSB)", Choice(notch));
        Item(54, "[NOTCH] Switch (AM)", Choice(notch));
        Item(55, "SSB/CW Synchronous Tuning", onOff);
        Item(56, "CW Normal Side", Choice("LSB", "USB"));
        Group("Function > Front Key Customize");
        Item(57, "[VOX/BK-IN]", Choice(new[] { "VOX/BK-IN", "RX-ANT", "APF", "PRESET" }.Concat(memories).ToArray()));
        Item(58, "[AUTOTUNE]", Choice(new[] { "AUTOTUNE", "RX-ANT", "APF", "PRESET" }.Concat(memories).ToArray()));
        Item(59, "[▲]", Choice(new[] { "M-CH UP", "M-CH DOWN", "RX-ANT", "APF", "PRESET" }.Concat(memories).ToArray()));
        Item(60, "[▼]", Choice(new[] { "M-CH UP", "M-CH DOWN", "RX-ANT", "APF", "PRESET" }.Concat(memories).ToArray()));
        Group("Function > MIC Key Customize");
        Item(61, "[UP]", Choice(micKeys));
        Item(62, "[DN]", Choice(micKeys));
        Group("Function");
        Item(63, "Screen Capture [POWER] SW", onOff);
        Item(64, "Screen Capture File Type", Choice("PNG", "BMP"));
        Item(65, "Keyboard Type", Choice("Ten-key", "Full Keyboard"));
        Item(66, "Full Keyboard Layout", Choice("English", "German", "French"));
        Item(67, "Calibration Marker", onOff);
        Item(68, "REF Adjust", percent);

        Group("Connectors > USB AF/IF Output");
        Item(69, "Output Select", Choice(outputSelect));
        Item(70, "AF Output Level", percent);
        Item(71, "AF SQL", Choice(afSql));
        Item(72, "AF Beep/Speech... Output", onOff);
        Item(73, "IF Output Level", percent);
        Group("Connectors > ACC AF/IF Output");
        Item(74, "Output Select", Choice(outputSelect));
        Item(75, "AF Output Level", percent);
        Item(76, "AF SQL", Choice(afSql));
        Item(77, "AF Beep/Speech... Output", onOff);
        Item(78, "IF Output Level", percent);
        Group("Connectors > LAN AF/IF Output");
        Item(79, "Output Select", Choice(outputSelect));
        Item(80, "AF SQL", Choice(afSql));
        Group("Connectors > MOD Input");
        Item(81, "USB MOD Level", percent, tx: true);
        Item(82, "ACC MOD Level", percent, tx: true);
        Item(83, "LAN MOD Level", percent, tx: true);
        Item(84, "DATA OFF MOD", Choice(modSource), tx: true);
        Item(85, "DATA MOD", Choice(modSource), tx: true);
        Group("Connectors > External Keypad");
        Item(86, "VOICE", onOff, tx: true);
        Item(87, "KEYER", onOff, tx: true);
        Item(88, "RTTY", onOff, tx: true);
        Group("Connectors > CI-V");
        Item(89, "CI-V Transceive", onOff, fix: link);
        Item(90, "USB/LAN→REMOTE Transceive Address", HexAddress(), fix: link);
        Item(91, "CI-V Output (for ANT)", onOff, fix: link);
        Item(92, "CI-V USB (A) Echo Back", onOff, fix: link);
        Item(93, "CI-V USB (B) Echo Back", onOff, fix: link);
        Group("Connectors");
        Item(94, "USB (B) Function", Choice("RTTY Decode", "CI-V"), fix: link);
        Item(95, "SEND Relay Output", onOff, tx: true);
        Group("Connectors > USB SEND/Keying");
        Item(96, "USB SEND", Choice(usbLine), tx: true);
        Item(97, "USB Keying (CW)", Choice(usbLine), tx: true);
        Item(98, "USB Keying (RTTY)", Choice(usbLine), tx: true);
        Group("Connectors");
        Item(99, "PTT Port Function", Choice("PTT Input", "PTT Input + SEND Output"), tx: true);

        Group("Network");
        Item(100, "DHCP", onOff, fix: link);
        Item(101, "IP Address", ip, fix: link);
        Item(102, "IP Address (from DHCP)", ip, fix: link);
        Item(103, "Subnet Mask", SubnetMask(), fix: link);
        Item(104, "Default Gateway", ip, fix: link);
        Item(105, "Primary DNS Server", ip, fix: link);
        Item(106, "Secondary DNS Server", ip, fix: link);
        Item(107, "Network Name", Text(15, "A-Z0-9_-"), fix: link);
        Group("Network > Remote Settings");
        Item(108, "Network Control", onOff, fix: link);
        Item(109, "Power OFF Setting (for Remote Control)", Choice("Only Shutdown", "Standby/Shutdown"), fix: link);
        Item(110, "Control Port (UDP)", Number(""), fix: link);
        Item(111, "Serial Port (UDP)", Number(""), fix: link);
        Item(112, "Audio Port (UDP)", Number(""), fix: link);
        Item(113, "Internet Access Line", Choice("FTTH", "ADSL/CATV"), fix: link);
        Item(114, "Network Radio Name", Text(16, " -~"), fix: link);

        Group("Display");
        Item(115, "LCD Backlight", percent);
        Item(116, "Display Type", Choice("A (Black background)", "B (Blue background)"));
        Item(117, "Display Font", Choice("Square", "Round"));
        Item(118, "Meter Peak Hold", onOff);
        Item(119, "Memory Name", onOff);
        Item(120, "MN-Q Popup (MN OFF→ON)", onOff);
        Item(121, "BW Popup (PBT)", onOff);
        Item(122, "BW Popup (FIL)", onOff);
        Item(123, "Screen Saver", Choice("OFF", "15 min", "30 min", "60 min"));
        Group("Display > External Display");
        Item(124, "External Display", onOff);
        Item(125, "Resolution", Choice("640x480", "1024x768", "1280x720"));
        Item(126, "Audio Output", onOff);
        Group("Display");
        Item(127, "Opening Message", onOff);
        Item(128, "My Call", Text(10, "A-Z0-9/@.-"));
        Item(129, "Power ON Check", onOff);
        Item(130, "Display Language", Choice("English", "Japanese"));
        Item(131, "System Language", Choice("English", "Japanese"));

        // The clock: saved so the file is complete, but it is not a setting
        // and a Load never writes it back.
        Group("Time Set > Date/Time");
        Item(132, "Date", Date(), fix: clock);
        Item(133, "Time", Time(), fix: clock);
        Item(134, "NTP Function", onOff);
        Item(135, "NTP Server Address", Text(64, "A-Za-z0-9.-", upper: false));
        Group("Time Set");
        Item(136, "UTC Offset", UtcOffset());

        Section("SCOPE SET");
        Group("SCOPE > EXPD/SET");
        Item(137, "Scope during Tx (CENTER Type)", onOff);
        Item(138, "Max Hold", Choice("OFF", "10s Hold", "ON"));
        Item(139, "CENTER Type Display", Choice("Filter Center", "Carrier Point Center", "Carrier Point Center (Abs. Freq.)"));
        Item(140, "Marker Position (Fix Type/SCROLL Type)", Choice("Filter Center", "Carrier Point"));
        Item(141, "VBW", Choice("Narrow", "Wide"));
        Item(142, "Averaging", Choice("OFF", "2", "3", "4"));
        Item(143, "Waveform Type", Choice("Fill", "Fill+Line"));
        Item(144, "Waveform Color (Current)", rgb);
        Item(145, "Waveform Color (Line)", rgb);
        Item(146, "Waveform Color (Max Hold)", rgb);
        Item(147, "Waterfall Display", onOff);
        Item(148, "Waterfall Speed", Choice("Slow", "Mid", "Fast"));
        Item(149, "Waterfall Size (Expand Screen)", Choice("Small", "Mid", "Large"));
        Item(150, "Waterfall Peak Color Level", Choice(Enumerable.Range(1, 8).Select(n => $"Grid {n}").ToArray()));
        Item(151, "Waterfall Marker Auto-hide", onOff);
        string[] edgeRanges =
        {
            "0.03-1.60", "1.60-2.00", "2.00-6.00", "6.00-8.00", "8.00-11.00", "11.00-15.00", "15.00-20.00",
            "20.00-22.00", "22.00-26.00", "26.00-30.00", "30.00-45.00", "45.00-60.00", "60.00-74.80",
        };
        int edge = 152;
        foreach (var range in edgeRanges)
        {
            Group($"SCOPE > EXPD/SET > Fixed Edges > {range} MHz");
            for (int n = 1; n <= 4; n++)
                Item(edge++, $"No.{n}", Edges());
        }

        Section("AUDIO SCOPE SET");
        Group("AUDIO > EXPD/SET");
        Item(204, "FFT Scope Waveform Type", Choice("Line", "Fill"));
        Item(205, "FFT Scope Waveform Color", rgb);
        Item(206, "FFT Scope Waterfall Display", onOff);
        Item(207, "Oscilloscope Waveform Color", rgb);

        Section("KEYER 001");
        Group("KEYER/DECODE > KEYER > EDIT/SET > 001 SET");
        Item(208, "Number Style", Choice("Normal", "190→ANO", "190→ANT", "90→NO", "90→NT"));
        for (int m = 1; m <= 8; m++)
            Item(208 + m, $"Count Up Trigger (M{m})", onOff);
        Item(217, "Present Number", Number("", 1, 9999, 2));

        Section("SET");
        Group("CW-KEY Set");
        Item(218, "Side Tone Level", percent);
        Item(219, "Side Tone Level Limit", onOff);
        Item(220, "Keyer Repeat Time", Number(" sec", 1, 60));
        Item(221, "Dot/Dash Ratio", DotDash(), tx: true);
        Item(222, "Rise Time", Choice("2 ms", "4 ms", "6 ms", "8 ms"), tx: true);
        Item(223, "Paddle Polarity", Choice("Normal", "Reverse"), tx: true);
        Item(224, "Key Type", Choice("Straight", "Bug", "Paddle"), tx: true);
        Item(225, "MIC Up/Down Keyer", onOff, tx: true);

        Section("KEYER DECODE SET");
        Group("KEYER/DECODE > KEYER > EDIT/SET");
        Item(226, "Decode Display", onOff);
        Item(227, "Japanese Morse Decode", onOff);

        Section("RTTY DECODE SET");
        Group("RTTY DECODE > EXPD/SET");
        Item(228, "FFT Scope Averaging", Choice("OFF", "2", "3", "4"));
        Item(229, "FFT Scope Waveform Color", rgb);
        Item(230, "Decode USOS", onOff);
        Item(231, "Decode New Line Code", Choice("CR, LF, CR+LF", "CR+LF"));
        Item(232, "TX USOS", onOff, tx: true);
        Item(233, "Font Color (Receive)", rgb);
        Item(234, "Font Color (Transmit)", rgb);

        Section("RTTY 001");
        Group("RTTY DECODE > TX MEM > EDIT/SET > 001 SET");
        for (int m = 1; m <= 8; m++)
            Item(234 + m, $"Count Up Trigger (RT{m})", onOff);
        Item(243, "Present Number", Number("", 1, 9999, 2));

        Section("RTTY DECODE LOG");
        Group("RTTY DECODE > LOG");
        Item(244, "Decode Log", onOff);
        Group("RTTY DECODE > LOG > Log Set");
        Item(245, "File Type", Choice("Text", "HTML"));
        Item(246, "Time Stamp", onOff);
        Item(247, "Time Stamp (Time)", Choice("Local", "UTC"));
        Item(248, "Time Stamp (Frequency)", onOff);

        Section("CW DECODE SET");
        Group("KEYER/DECODE > CW DECODE > SET");
        Item(249, "FFT Scope Waveform Color", rgb);
        Item(250, "Signal Level Waveform Color", rgb);
        Item(251, "Font Color (Receive)", rgb);
        Item(252, "Font Color (Transmit)", rgb);

        Section("SCAN SET");
        Group("SCAN > SET");
        Item(253, "SCAN Speed", Choice("Slow", "Fast"));
        Item(254, "SCAN Resume", onOff);

        Section("VOICE TX");
        Group("VOICE > TX LEVEL");
        Item(255, "TX LEVEL", percent, tx: true);
        Group("VOICE > REC/SET > SET");
        Item(256, "Auto Monitor", onOff);
        Item(257, "Repeat Time", Number(" sec", 1, 15));

        Section("RECORD");
        Group("Recorder Set");
        Item(258, "TX REC Audio", Choice("Direct", "Monitor"));
        Item(259, "RX REC Condition", Choice("Always", "Squelch Auto"));
        Item(260, "File Split", onOff);
        Item(261, "PTT Auto REC", onOff);
        Item(262, "PRE-REC for PTT Auto REC", Choice("OFF", "5 sec", "10 sec", "15 sec"));
        Group("Player Set");
        Item(263, "Skip Time", Choice("3 sec", "5 sec", "10 sec", "30 sec"));

        Section("MENU SET");
        Group("MENU");
        Item(264, "CW [KEYER/DECODE] Key", Choice("Select screen", "KEYER (hold: Select screen)", "DECODE (hold: Select screen)"));

        Section("FUNCTION");
        Group("NB");
        Item(265, "DEPTH", Offset(1, "", 9));
        Item(266, "WIDTH", Scaled(1, 100, ""));
        Group("VOX");
        Item(267, "Delay", Tenths(" sec", 20), tx: true);
        Item(268, "Voice Delay", Choice("OFF", "Short", "Mid", "Long"), tx: true);
        Group("APF");
        Item(269, "TYPE", Choice("SHARP", "SOFT"));
        Item(270, "AF LEVEL", Number(" dB", 0, 6));

        return items;
    }

    // -- Codecs ----------------------------------------------------------------
    // Every decoder falls back to the bytes in hex when the reply is not the
    // shape the manual gives, so an unexpected answer is still saved as sent.
    // Values lists what Load and the editor may send; an item without them can
    // only be put back as it was saved.

    private static string Hex(byte[] d) => RadioMenuFile.Hex(d);

    private static int Bcd(byte b) => ((b >> 4) & 0x0F) * 10 + (b & 0x0F);

    private static bool IsBcd(byte b) => (b >> 4) <= 9 && (b & 0x0F) <= 9;

    private static byte ToBcd(int v) => (byte)(((v / 10) << 4) | (v % 10));

    /// <summary>A number as <paramref name="bytes"/> bytes of BCD, most significant first: 255 = 02 55.</summary>
    private static byte[] BigBcdBytes(long v, int bytes)
    {
        var d = new byte[bytes];
        for (int i = bytes - 1; i >= 0; i--)
        {
            d[i] = ToBcd((int)(v % 100));
            v /= 100;
        }
        return d;
    }

    private static IReadOnlyList<byte[]> Range(long min, long max, int bytes)
    {
        var list = new List<byte[]>();
        for (long v = min; v <= max; v++) list.Add(BigBcdBytes(v, bytes));
        return list;
    }

    /// <summary>The bytes as one decimal number, most significant first: 02 55 = 255.</summary>
    private static long? BigBcd(ReadOnlySpan<byte> d)
    {
        if (d.Length == 0) return null;
        long v = 0;
        foreach (var b in d)
        {
            if (!IsBcd(b)) return null;
            v = v * 100 + Bcd(b);
        }
        return v;
    }

    /// <summary>Little-endian BCD in 100 Hz units, as frequencies are sent: 50 52 01 = 15 250.0 kHz.</summary>
    private static long? FrequencyHz(ReadOnlySpan<byte> d)
    {
        long v = 0, scale = 1;
        foreach (var b in d)
        {
            if (!IsBcd(b)) return null;
            v += Bcd(b) * scale;
            scale *= 100;
        }
        return v * 100;
    }

    private static RadioMenuCodec Choice(params string[] names) => new(
        d => d.Length == 1 && IsBcd(d[0]) && Bcd(d[0]) < names.Length ? names[Bcd(d[0])] : Hex(d),
        Range(0, names.Length - 1, 1));

    /// <summary>A number with no range given: shown, never sent.</summary>
    private static RadioMenuCodec Number(string unit) => new(
        d => BigBcd(d) is long v ? $"{v}{unit}" : Hex(d));

    private static RadioMenuCodec Number(string unit, int min, int max, int bytes = 1) => new(
        d => d.Length == bytes && BigBcd(d) is long v && v >= min && v <= max ? $"{v}{unit}" : Hex(d),
        Range(min, max, bytes));

    /// <summary>00–<paramref name="max"/> shown from <paramref name="first"/>: DEPTH 00–09 = 1–10.</summary>
    private static RadioMenuCodec Offset(int first, string unit, int max) => new(
        d => d.Length == 1 && BigBcd(d) is long v && v <= max ? $"{v + first}{unit}" : Hex(d),
        Range(0, max, 1));

    /// <summary>00–<paramref name="max"/> in tenths: VOX Delay 00–20 = 0.0–2.0 sec.</summary>
    private static RadioMenuCodec Tenths(string unit, int max) => new(
        d => d.Length == 1 && BigBcd(d) is long v && v <= max ? $"{v / 10.0:0.0}{unit}" : Hex(d),
        Range(0, max, 1));

    /// <summary>00 00–02 55 shown on the radio as min–max.</summary>
    private static RadioMenuCodec Scaled(int min, int max, string unit) => new(
        d => d.Length == 2 && BigBcd(d) is long v && v <= 255
            ? $"{Math.Round(min + v * (max - min) / 255.0)}{unit}" : Hex(d),
        Range(0, 255, 2));

    private static RadioMenuCodec Percent() => Scaled(0, 100, "%");

    /// <summary>00–10 = −5…+5.</summary>
    private static RadioMenuCodec Tone() => new(
        d => d.Length == 1 && IsBcd(d[0]) && Bcd(d[0]) <= 10 ? (Bcd(d[0]) - 5).ToString("+0;-0;0") : Hex(d),
        Range(0, 10, 1));

    /// <summary>HH LL: HPF 00 = Through, 01–20 = 100–2000 Hz; LPF 05–24 = 500–2400 Hz, 25 = Through.</summary>
    private static RadioMenuCodec HpfLpf()
    {
        var values = new List<byte[]>();
        // The high-pass edge below the low-pass one, or the passband would be empty.
        for (int h = 0; h <= 20; h++)
            for (int l = 5; l <= 25; l++)
                if (h < l) values.Add(new[] { ToBcd(h), ToBcd(l) });
        return new(d =>
        {
            if (d.Length != 2 || !IsBcd(d[0]) || !IsBcd(d[1])) return Hex(d);
            int h = Bcd(d[0]), l = Bcd(d[1]);
            if (h > 20 || l is < 5 or > 25) return Hex(d);
            string hpf = h == 0 ? "Through" : $"{h * 100} Hz";
            string lpf = l == 25 ? "Through" : $"{l * 100} Hz";
            return $"HPF {hpf} / LPF {lpf}";
        }, values);
    }

    /// <summary>
    /// One byte: high digit the higher edge, low digit the lower edge. The
    /// p. 20 diagram reads the other way round, but the radio's defaults
    /// (WIDE 100-2900, MID 300-2700, NAR 500-2500) come back as 30, 14, 05.
    /// </summary>
    private static RadioMenuCodec Tbw()
    {
        int[] low = { 100, 120, 150, 200, 300, 500 };
        int[] high = { 2500, 2700, 2800, 2900 };
        var values = new List<byte[]>();
        for (int hi = 0; hi < high.Length; hi++)
            for (int lo = 0; lo < low.Length; lo++)
                values.Add(new[] { (byte)((hi << 4) | lo) });
        return new(d =>
        {
            if (d.Length != 1) return Hex(d);
            int hi = d[0] >> 4, lo = d[0] & 0x0F;
            return lo < low.Length && hi < high.Length ? $"{low[lo]}-{high[hi]} Hz" : Hex(d);
        }, values);
    }

    /// <summary>Three bytes of frequency then a direction byte (00 = +, 01 = −).</summary>
    private static RadioMenuCodec SplitOffset() => new(d =>
    {
        if (d.Length != 4 || d[3] > 1 || FrequencyHz(d.AsSpan(0, 3)) is not long hz) return Hex(d);
        return $"{(d[3] == 1 ? "-" : "+")}{hz / 1_000_000.0:0.000} MHz";
    });

    /// <summary>Two edges of three bytes each, lower then higher.</summary>
    private static RadioMenuCodec Edges() => new(d =>
    {
        if (d.Length != 6 || FrequencyHz(d.AsSpan(0, 3)) is not long lo || FrequencyHz(d.AsSpan(3, 3)) is not long hi)
            return Hex(d);
        return $"{lo / 1_000_000.0:0.000}-{hi / 1_000_000.0:0.000} MHz";
    });

    /// <summary>Four octets of two bytes each, 00 00–02 55; FF is blank.</summary>
    private static RadioMenuCodec Ip() => new(d =>
    {
        if (d.Length == 0 || d.All(b => b == 0xFF)) return "(blank)";
        if (d.Length != 8) return Hex(d);
        var octets = new long[4];
        for (int i = 0; i < 4; i++)
        {
            if (BigBcd(d.AsSpan(i * 2, 2)) is not long o || o > 255) return Hex(d);
            octets[i] = o;
        }
        return string.Join(".", octets);
    });

    /// <summary>01–30 = the number of mask bits.</summary>
    private static RadioMenuCodec SubnetMask() => new(d =>
    {
        if (d.Length != 1 || !IsBcd(d[0]) || Bcd(d[0]) is < 1 or > 30) return Hex(d);
        int bits = Bcd(d[0]);
        uint mask = uint.MaxValue << (32 - bits);
        return $"{mask >> 24}.{(mask >> 16) & 0xFF}.{(mask >> 8) & 0xFF}.{mask & 0xFF} ({bits} bit)";
    });

    private static readonly System.Text.RegularExpressions.Regex RgbText = new(
        @"^\s*R\s*(\d{1,3})\s*G\s*(\d{1,3})\s*B\s*(\d{1,3})\s*$",
        System.Text.RegularExpressions.RegexOptions.IgnoreCase);

    /// <summary>R, G and B, two bytes each, 00 00–02 55. Typed back as "R 255 G 128 B 0".</summary>
    private static RadioMenuCodec Rgb() => new(d =>
    {
        if (d.Length != 6) return Hex(d);
        var c = new long[3];
        for (int i = 0; i < 3; i++)
        {
            if (BigBcd(d.AsSpan(i * 2, 2)) is not long v || v > 255) return Hex(d);
            c[i] = v;
        }
        return $"R {c[0]} G {c[1]} B {c[2]}";
    }, Parse: t =>
    {
        var m = RgbText.Match(t);
        if (!m.Success) return null;
        var d = new List<byte>();
        for (int i = 1; i <= 3; i++)
        {
            int v = int.Parse(m.Groups[i].Value);
            if (v > 255) return null;
            d.AddRange(BigBcdBytes(v, 2));
        }
        return d.ToArray();
    });

    /// <summary>00 00–02 23 = 00h–DFh.</summary>
    private static RadioMenuCodec HexAddress() => new(
        d => BigBcd(d) is long v && v <= 0xDF ? $"{v:X2}h" : Hex(d));

    /// <summary>28–45 = 1:1:2.8–1:1:4.5.</summary>
    private static RadioMenuCodec DotDash() => new(
        d => d.Length == 1 && IsBcd(d[0]) && Bcd(d[0]) is >= 28 and <= 45 ? $"1:1:{Bcd(d[0]) / 10.0:0.0}" : Hex(d),
        Range(28, 45, 1));

    /// <summary>
    /// ASCII, up to <paramref name="max"/> characters from <paramref name="chars"/>
    /// (a regex character class body, '-' last, from the manual's p. 18 table).
    /// </summary>
    private static RadioMenuCodec Text(int max, string chars, bool upper = true) => new(d =>
    {
        if (!d.All(b => b is >= 0x20 and < 0x7F)) return Hex(d);
        var s = System.Text.Encoding.ASCII.GetString(d).Trim();
        return s.Length == 0 ? "(blank)" : s;
    }, TextMax: max, TextChars: chars, Upper: upper);

    /// <summary>20 26 10 25 = 2026/10/25.</summary>
    private static RadioMenuCodec Date() => new(
        d => d.Length == 4 && d.All(IsBcd) ? $"{Bcd(d[0]):00}{Bcd(d[1]):00}-{Bcd(d[2]):00}-{Bcd(d[3]):00}" : Hex(d));

    /// <summary>23 59 = 23:59.</summary>
    private static RadioMenuCodec Time() => new(
        d => d.Length == 2 && d.All(IsBcd) ? $"{Bcd(d[0]):00}:{Bcd(d[1]):00}" : Hex(d));

    /// <summary>Hours and minutes (00 00–14 00) then a direction byte (00 = +, 01 = −).</summary>
    private static RadioMenuCodec UtcOffset() => new(
        d => d.Length == 3 && IsBcd(d[0]) && IsBcd(d[1]) && d[2] <= 1
            ? $"{(d[2] == 1 ? "-" : "+")}{Bcd(d[0]):00}:{Bcd(d[1]):00}" : Hex(d));
}
