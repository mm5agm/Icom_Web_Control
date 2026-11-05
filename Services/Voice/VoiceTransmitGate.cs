using Icom_Web_Control.Services.Civ;

namespace Icom_Web_Control.Services.Voice
{
    /// <summary>
    /// Commands that put the radio on the air need a much clearer match than the
    /// rest. The normal gate is low because a quiet or distant mic scores real
    /// commands low, and a misheard "noise reduction on" does little harm. A
    /// misheard transmit is a different matter: the recogniser always picks its
    /// closest phrase, and wrong phrases have been seen scoring 0.62.
    ///
    /// Commands that stop transmitting are deliberately not covered, so getting
    /// off the air stays as easy as it was.
    /// </summary>
    public static class VoiceTransmitGate
    {
        public const float MinConfidence = 0.85f;

        /// <summary>True if this command can start a transmission.</summary>
        /// <param name="intent">The command, after normalising.</param>
        /// <param name="macroCat">The CI-V of a macro, if it is one.</param>
        /// <param name="atuTuning">
        /// A tuner cycle is running. The tune phrase then stops it, and stopping
        /// has to stay easy.
        /// </param>
        public static bool StartsTransmit(string intent, string? macroCat, bool atuTuning = false) => intent switch
        {
            "TxOn" => true,
            "AtuTune" => !atuTuning,
            "Macro" => MacroStartsTransmit(macroCat),
            _ => false,
        };

        private static bool MacroStartsTransmit(string? cat)
        {
            // One that won't parse is never sent: the dispatcher rejects it.
            if (!CivMacroCodec.TryParse(cat, out var commands, out _)) return false;
            return commands.Any(CommandStartsTransmit);
        }

        private static bool CommandStartsTransmit(byte[] c) => c switch
        {
            // 1C 00 nn: PTT. 00 is receive; with no data it only reads the state.
            [0x1C, 0x00, var state, ..] => state != 0x00,
            // 1C 01 02: start a tuner cycle.
            [0x1C, 0x01, 0x02, ..] => true,
            // 16 46 nn: VOX. On, sound in the room can key the radio.
            [0x16, 0x46, var vox, ..] => vox != 0x00,
            // 17: send CW. 17 FF stops sending.
            [0x17, 0xFF, ..] => false,
            [0x17, _, ..] => true,
            // 28 00 nn: play a voice memory. 00 stops it.
            [0x28, 0x00, var memory, ..] => memory != 0x00,
            _ => false,
        };
    }
}
