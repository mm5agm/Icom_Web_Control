using System;
using System.Collections.Generic;

namespace Icom_Web_Control.Services.Civ
{
    /// <summary>A stretch of frequency a radio can receive on.</summary>
    public readonly record struct ReceiveRange(long LoHz, long HiHz);

    /// <summary>A stretch of frequency a radio can transmit on, and its most power there.</summary>
    public readonly record struct TransmitRange(long LoHz, long HiHz, int MaxWatts);

    /// <summary>
    /// What one radio tunes and transmits on, and the bands and modes it has
    /// beyond the set the IC-7300 family has.
    /// </summary>
    public sealed record RadioFrequencies(
        IReadOnlyList<ReceiveRange> Receive,
        IReadOnlyList<TransmitRange> Transmit,
        IReadOnlyList<string> ExtraBands,
        IReadOnlyList<string> ExtraModes,
        long OnePreampFromHz = 0)
    {
        /// <summary>Modes that are not on every radio, so have to be on the radio's own list.</summary>
        public static readonly IReadOnlySet<string> ModesOnlySomeHave =
            new HashSet<string>(StringComparer.OrdinalIgnoreCase) { "WFM", "DV" };

        /// <summary>The HF and 6 and 4 m bands IWC has buttons for, which every HF radio has.</summary>
        public static readonly IReadOnlyList<string> HfBands = new[]
            { "160m", "80m", "60m", "40m", "30m", "20m", "17m", "15m", "12m", "10m", "6m", "4m" };

        /// <summary>
        /// The band buttons for this radio: the HF set when it receives on 160 m,
        /// then the bands it adds. A VHF/UHF radio like the IC-9700 gets only its own.
        /// </summary>
        public IReadOnlyList<string> Bands
        {
            get
            {
                var list = new List<string>();
                if (Receive[0].LoHz <= 1_800_000) list.AddRange(HfBands);
                list.AddRange(ExtraBands);
                return list;
            }
        }

        public static readonly RadioFrequencies Ic7300Family = new(
            new[] { new ReceiveRange(30_000, 74_800_000) },
            new[]
            {
                new TransmitRange(1_800_000, 29_700_000, 100),
                new TransmitRange(50_000_000, 54_000_000, 100),
            },
            Array.Empty<string>(),
            Array.Empty<string>());

        /// <summary>
        /// IC-7100, from the specifications in section 21 of its manual. Receive stops
        /// at 199.999999 MHz and starts again at 400 MHz. What it can transmit on
        /// depends on the radio's version; these are the widest ranges. On 144 and
        /// 430 MHz it has one preamp, not P.AMP1 and P.AMP2 (section 5).
        /// </summary>
        public static readonly RadioFrequencies Ic7100 = new(
            new[]
            {
                new ReceiveRange(30_000, 199_999_999),
                new ReceiveRange(400_000_000, 470_000_000),
            },
            new[]
            {
                new TransmitRange(1_800_000, 29_700_000, 100),
                new TransmitRange(50_000_000, 54_000_000, 100),
                new TransmitRange(70_000_000, 70_500_000, 50),
                new TransmitRange(144_000_000, 148_000_000, 50),
                new TransmitRange(430_000_000, 450_000_000, 35),
            },
            new[] { "2m", "70cm" },
            new[] { "WFM", "DV" },
            OnePreampFromHz: 144_000_000);

        /// <summary>
        /// IC-9700, from the specifications in its manual: 2 m, 70 cm and 23 cm,
        /// receive and transmit alike. These are the widest ranges; what a radio
        /// may transmit on depends on its version. It has one preamp on every
        /// band (16 02 01; 02 and 03 are an external preamp, which IWC leaves alone).
        /// </summary>
        public static readonly RadioFrequencies Ic9700 = new(
            new[]
            {
                new ReceiveRange(144_000_000, 148_000_000),
                new ReceiveRange(430_000_000, 450_000_000),
                new ReceiveRange(1_240_000_000, 1_300_000_000),
            },
            new[]
            {
                new TransmitRange(144_000_000, 148_000_000, 100),
                new TransmitRange(430_000_000, 450_000_000, 75),
                new TransmitRange(1_240_000_000, 1_300_000_000, 10),
            },
            new[] { "2m", "70cm", "23cm" },
            new[] { "DV" },
            OnePreampFromHz: 144_000_000);
    }
}
