namespace Icom_Web_Control.Services.Civ
{
    /// <summary>
    /// The IC-7100's memory channels (1A 00), from page 20-16 of its manual. Five
    /// banks of 99, and a longer record than a 7300's. IWC numbers them from 1
    /// through the banks: 1 is A-01, 100 is B-01, 495 is E-99. Read only: nothing
    /// here builds a write.
    /// </summary>
    public static class Ic7100MemoryCodec
    {
        public const int Banks = 5;
        public const int ChannelsPerBank = 99;

        /// <summary>Data bytes in a read: 00, bank, then the channel in two BCD bytes.</summary>
        public const int ReadDataLength = 4;

        // Offsets into the reply's data, which starts with the 00 sub-command.
        private const int RecordAt = 4;         // split and select
        private const int FrequencyAt = 5;      // 5 bytes
        private const int ModeAt = 10;
        private const int FilterAt = 11;
        private const int DataModeAt = 12;
        private const int BlockLength = 47;     // frequency through the three call signs
        private const int TxBlockAt = FrequencyAt + BlockLength;
        private const int NameAt = TxBlockAt + BlockLength;
        private const int NameLength = 16;

        private static int Bank(int channel) => (channel - 1) / ChannelsPerBank + 1;
        private static int InBank(int channel) => (channel - 1) % ChannelsPerBank + 1;

        /// <summary>"A-01" for channel 1, "B-01" for channel 100.</summary>
        public static string Label(int channel) => $"{(char)('A' + Bank(channel) - 1)}-{InBank(channel):00}";

        /// <summary>The data bytes that ask for a channel.</summary>
        public static byte[] ReadData(int channel)
        {
            int n = InBank(channel);
            return new byte[]
            {
                CivProtocol.SubMemoryChannel,
                (byte)Bank(channel),
                0x00,
                (byte)(((n / 10) << 4) | (n % 10)),
            };
        }

        /// <summary>
        /// True only for the exact bytes of a read: 00, a bank, 00, a channel in BCD.
        /// A 7300's clear (00 00 ch FF) is the same length, so the length alone won't do.
        /// </summary>
        public static bool IsRead(ReadOnlySpan<byte> data)
        {
            if (data.Length != ReadDataLength || data[0] != CivProtocol.SubMemoryChannel) return false;
            if (data[1] is < 1 or > Banks || data[2] != 0x00) return false;
            int tens = data[3] >> 4, ones = data[3] & 0x0F;
            return tens <= 9 && ones <= 9 && data[3] != 0x00;
        }

        /// <summary>
        /// The channel in a reply. Null when the reply isn't for this channel or is
        /// too short to read, which is a miss and not an empty channel.
        /// </summary>
        public static RadioMemoryChannel? Parse(int channel, byte[] data)
        {
            var asked = ReadData(channel);
            if (data.Length < ReadDataLength) return null;
            for (int i = 0; i < ReadDataLength; i++)
                if (data[i] != asked[i]) return null;

            // Empty: the number alone, or the number and FF.
            if (data.Length == ReadDataLength || data[RecordAt] == 0xFF)
                return new RadioMemoryChannel { Channel = channel, IsEmpty = true };
            if (data.Length <= DataModeAt) return null;
            if (data[FrequencyAt] == 0xFF)
                return new RadioMemoryChannel { Channel = channel, IsEmpty = true };

            long hz = CivProtocol.DecodeBcd(data.AsSpan(FrequencyAt, 5));
            string mode = CivRadioController.NameForMode(data[ModeAt], data[DataModeAt] != 0);

            long txHz = hz;
            string txMode = mode;
            if (data.Length > TxBlockAt + 7)
            {
                txHz = CivProtocol.DecodeBcd(data.AsSpan(TxBlockAt, 5));
                txMode = CivRadioController.NameForMode(data[TxBlockAt + 5], data[TxBlockAt + 7] != 0);
            }

            return new RadioMemoryChannel
            {
                Channel = channel,
                IsEmpty = false,
                FrequencyHz = hz,
                Mode = mode,
                Filter = data[FilterAt] is >= 1 and <= 3 ? data[FilterAt] : 1,
                SplitOn = (data[RecordAt] >> 4) != 0,
                TxFrequencyHz = txHz,
                TxMode = txMode,
                Name = data.Length >= NameAt + NameLength
                    ? CivRadioController.DecodeName(data.AsSpan(NameAt, NameLength))
                    : "",
            };
        }
    }
}
