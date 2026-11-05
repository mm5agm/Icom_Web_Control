using System;
using System.Collections.Generic;

namespace Icom_Web_Control.Services.Civ
{
    /// <summary>
    /// What one radio model is, and which CI-V frames IWC may send it.
    ///
    /// IWC's CI-V code was written for the IC-7300 family. A model whose command
    /// table differs gets an allow-list, written from that radio's manual. A frame
    /// not on the list is never sent, so 7300 bytes cannot change the wrong setting
    /// on another radio. That matters most for 1A 05: the item numbers are different
    /// on every model, and a radio accepts an item number it reads differently.
    /// </summary>
    public sealed class RadioProfile
    {
        private readonly IReadOnlyDictionary<byte, CommandRule>? _allowed;

        private RadioProfile(string model, byte civAddress, byte attenuatorOn, bool hasScope,
            RadioFrequencies frequencies, IReadOnlyDictionary<byte, CommandRule>? allowed)
        {
            Model = model;
            CivAddress = civAddress;
            AttenuatorOn = attenuatorOn;
            HasScope = hasScope;
            Frequencies = frequencies;
            _allowed = allowed;
        }

        public string Model { get; }

        /// <summary>
        /// The radio's default CI-V address, which is the ID byte its 19 00 reply
        /// gives. A radio whose address has been changed from the default identifies
        /// by the new one, so it is not recognised as this model.
        /// </summary>
        public byte CivAddress { get; }

        /// <summary>The data byte for attenuator on, which is its size in dB as BCD: 20 dB is 0x20.</summary>
        public byte AttenuatorOn { get; }

        /// <summary>Whether the radio has a spectrum scope it can send over CI-V.</summary>
        public bool HasScope { get; }

        /// <summary>What the radio tunes and transmits on, and the bands and modes it adds to the HF set.</summary>
        public RadioFrequencies Frequencies { get; }

        /// <summary>The name and model number Hamlib knows this radio by, for rigctld clients.</summary>
        public (string Name, int Id) Hamlib => Model == "IC-7100" ? ("IC-7100", 3070) : ("IC-7300", 3073);

        /// <summary>The attenuator's size. The CI-V value is the dB figure in BCD.</summary>
        public int AttenuatorDb => (AttenuatorOn >> 4) * 10 + (AttenuatorOn & 0x0F);

        /// <summary>True when the radio has the CW audio peak filter (16 32).</summary>
        public bool HasApf => AllowsCommand(0x16, 0x32);

        /// <summary>How many memory banks the radio has. 0 means one plain list of 99.</summary>
        public int MemoryBanks => Model == "IC-7100" ? Ic7100MemoryCodec.Banks : 0;

        /// <summary>How many memory channels IWC can read, numbered from 1 through the banks.</summary>
        public int MemoryChannels => MemoryBanks > 0 ? MemoryBanks * Ic7100MemoryCodec.ChannelsPerBank : 99;

        /// <summary>True when IWC knows how to write this radio's memory channels.</summary>
        public bool CanWriteMemories => MemoryBanks == 0;

        /// <summary>A channel's name when it has none of its own: "CH 001", or "A-01" on a radio with banks.</summary>
        public string ChannelLabel(int channel) =>
            MemoryBanks > 0 ? Ic7100MemoryCodec.Label(channel) : $"CH {channel:000}";

        /// <summary>True when the radio can offset its transmit frequency (21 02).</summary>
        public bool HasDeltaTx => AllowsCommand(0x21, 0x02);

        /// <summary>How many preamps the radio has on this frequency.</summary>
        public int PreampSteps(long hz) =>
            Frequencies.OnePreampFromHz > 0 && hz >= Frequencies.OnePreampFromHz ? 1 : 2;

        /// <summary>
        /// The most power on this frequency. Off the transmit bands it is the
        /// radio's highest, so the HF figure, as nothing goes out there anyway.
        /// </summary>
        public int MaxWattsAt(long hz)
        {
            int highest = 0;
            foreach (var range in Frequencies.Transmit)
            {
                if (hz >= range.LoHz && hz <= range.HiHz) return range.MaxWatts;
                highest = Math.Max(highest, range.MaxWatts);
            }
            return highest;
        }

        /// <summary>The radio's RF power level is a percentage of the most on that band.</summary>
        public int PercentFor(long hz, int watts) =>
            (int)Math.Round(watts * 100.0 / MaxWattsAt(hz), MidpointRounding.AwayFromZero);

        /// <summary>The watts a percentage of RF power level means on this frequency.</summary>
        public int WattsFor(long hz, int percent) =>
            (int)Math.Round(percent * MaxWattsAt(hz) / 100.0, MidpointRounding.AwayFromZero);

        private bool AllowsCommand(byte command, byte sub) =>
            _allowed == null || (_allowed.TryGetValue(command, out var rule) && rule.Allows(true, sub));

        /// <summary>The lowest frequency the radio receives.</summary>
        public long MinHz => Frequencies.Receive[0].LoHz;

        /// <summary>The highest frequency the radio receives. There can be a gap below it: see <see cref="CanTune"/>.</summary>
        public long MaxHz => Frequencies.Receive[^1].HiHz;

        /// <summary>True when the radio can receive on this frequency.</summary>
        public bool CanTune(long hz)
        {
            foreach (var range in Frequencies.Receive)
                if (hz >= range.LoHz && hz <= range.HiHz)
                    return true;
            return false;
        }

        /// <summary>
        /// True when the radio has this mode. Only the modes one model adds need
        /// asking about: an IC-7300 would refuse WFM and DV, so it is never sent them.
        /// </summary>
        public bool HasMode(string mode) =>
            !RadioFrequencies.ModesOnlySomeHave.Contains(mode) || Contains(Frequencies.ExtraModes, mode);

        private static bool Contains(IReadOnlyList<string> list, string value)
        {
            foreach (var item in list)
                if (string.Equals(item, value, StringComparison.OrdinalIgnoreCase))
                    return true;
            return false;
        }

        /// <summary>The MkII: IWC's reference radio, with every command available.</summary>
        public static readonly RadioProfile Ic7300Mk2 = new("IC-7300MK2", 0xB6, CivProtocol.AttOn20dB, true, RadioFrequencies.Ic7300Family, null);

        /// <summary>The original IC-7300, which takes the same commands from IWC as the MkII.</summary>
        public static readonly RadioProfile Ic7300 = new("IC-7300", 0x94, CivProtocol.AttOn20dB, true, RadioFrequencies.Ic7300Family, null);

        /// <summary>
        /// IC-7100, from section 20 of its manual. It has no scope commands (27) and
        /// no voice keyer (28). Its whole 1A 05 table differs from the 7300's, so no
        /// item is allowed until it has been mapped; features that use one read as
        /// unavailable. Memories (1A 00) have their own layout: they can be read, and
        /// Allows refuses any 1A 00 frame that could write or clear one.
        /// </summary>
        public static readonly RadioProfile Ic7100 = new("IC-7100", 0x88, 0x12, false, RadioFrequencies.Ic7100,
            new Dictionary<byte, CommandRule>
            {
                [0x03] = CommandRule.Any,                                   // read frequency
                [0x04] = CommandRule.Any,                                   // read mode
                [0x05] = CommandRule.Any,                                   // set frequency
                [0x06] = CommandRule.Any,                                   // set mode
                [0x07] = CommandRule.Subs(0x00, 0x01, 0xA0, 0xB0),          // VFO A, B, A=B, A/B
                [0x0F] = CommandRule.ReadOrSubs(0x00, 0x01),                // split
                [0x11] = CommandRule.ReadOrSubs(0x00, 0x12),                // attenuator: off, 12 dB
                [0x14] = CommandRule.Subs(0x01, 0x02, 0x03, 0x06, 0x07, 0x08, 0x09, 0x0A, 0x0B, 0x0C,
                                          0x0D, 0x0E, 0x0F, 0x12, 0x15, 0x16, 0x17),   // levels
                [0x15] = CommandRule.Subs(0x02, 0x11, 0x12, 0x13, 0x14, 0x15, 0x16),   // meters
                [0x16] = CommandRule.Subs(0x02, 0x12, 0x22, 0x40, 0x41, 0x42, 0x43, 0x44, 0x45,
                                          0x46, 0x47, 0x48, 0x56, 0x57),               // functions (no APF, 32)
                [0x17] = CommandRule.Any,                                   // CW send
                [0x18] = CommandRule.Subs(0x00, 0x01),                      // power off, on
                [0x19] = CommandRule.Subs(0x00),                            // read ID
                [0x1A] = CommandRule.Subs(0x00, 0x03, 0x04, 0x06),          // memory read, filter width, AGC time, data mode
                [0x1B] = CommandRule.Subs(0x00, 0x01),                      // repeater and TSQL tone
                [0x1C] = CommandRule.Subs(0x00, 0x01),                      // TX status, tuner
                [0x21] = CommandRule.Subs(0x00, 0x01),                      // RIT (no delta TX, 02)
                [0x25] = CommandRule.Any,                                   // VFO frequency
                [0x26] = CommandRule.Any,                                   // VFO mode
            });

        private static readonly RadioProfile[] Known = { Ic7300Mk2, Ic7300, Ic7100 };

        /// <summary>The profile for a model name, or null for a radio IWC has no table for.</summary>
        public static RadioProfile? ForModel(string? model)
        {
            foreach (var profile in Known)
                if (string.Equals(profile.Model, model, StringComparison.OrdinalIgnoreCase))
                    return profile;
            return null;
        }

        /// <summary>
        /// The profile for a model name, and for anything unknown the MkII, because
        /// that is what IWC's limits have always been.
        /// </summary>
        public static RadioProfile OrDefault(string? model) => ForModel(model) ?? Ic7300Mk2;

        /// <summary>The model name for the ID byte in a radio's 19 00 reply.</summary>
        public static string ModelNameForId(byte idByte)
        {
            foreach (var profile in Known)
                if (profile.CivAddress == idByte)
                    return profile.Model;
            return $"Icom({idByte:X2})";
        }

        /// <summary>
        /// True when this whole frame (FE FE to from cmd ... FD, with any number of
        /// leading FE) may go to this radio. A model with a list refuses a frame it
        /// cannot read.
        /// </summary>
        public bool Allows(byte[] frame)
        {
            if (_allowed == null) return true;

            int preamble = 0;
            while (preamble < frame.Length && frame[preamble] == CivProtocol.Preamble) preamble++;
            int cmdAt = preamble + 2;  // past To and From
            if (preamble < 2 || cmdAt >= frame.Length - 1 || frame[^1] != CivProtocol.End) return false;

            if (!_allowed.TryGetValue(frame[cmdAt], out var rule)) return false;
            bool hasData = cmdAt + 1 < frame.Length - 1;
            // Memories are read-only here: only a well-formed read of one channel.
            if (!CanWriteMemories && hasData && frame[cmdAt] == CivProtocol.CmdMenu
                && frame[cmdAt + 1] == CivProtocol.SubMemoryChannel
                && !Ic7100MemoryCodec.IsRead(frame.AsSpan(cmdAt + 1, frame.Length - 1 - (cmdAt + 1))))
                return false;
            return rule.Allows(hasData, hasData ? frame[cmdAt + 1] : (byte)0);
        }

        private sealed class CommandRule
        {
            private readonly bool _any;
            private readonly bool _bareOk;
            private readonly HashSet<byte> _subs;

            private CommandRule(bool any, bool bareOk, byte[] subs)
            {
                _any = any;
                _bareOk = bareOk;
                _subs = new HashSet<byte>(subs);
            }

            /// <summary>Every frame of this command.</summary>
            public static readonly CommandRule Any = new(true, true, Array.Empty<byte>());

            /// <summary>Only these sub-commands: the first byte after the command must be one.</summary>
            public static CommandRule Subs(params byte[] subs) => new(false, false, subs);

            /// <summary>The bare command, which is a read, or these first data bytes.</summary>
            public static CommandRule ReadOrSubs(params byte[] subs) => new(false, true, subs);

            public bool Allows(bool hasData, byte first) => _any || (hasData ? _subs.Contains(first) : _bareOk);
        }
    }
}
