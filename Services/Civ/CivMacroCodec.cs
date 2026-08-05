using System;
using System.Collections.Generic;
using System.Globalization;

namespace Icom_Web_Control.Services.Civ
{
    /// <summary>
    /// Parses the payload string a voice macro (Settings → Voice Control →
    /// Custom Commands) carries into CI-V command bodies.
    ///
    /// The macro system is the one fully data-driven extension point in voice
    /// control — arbitrary phrases to an arbitrary radio command, no code
    /// change. On the Yaesu original the payload was an ASCII CAT string
    /// ("NR01;"); on Icom the same field holds the CI-V <b>command body</b> in
    /// hex — command byte, optional sub-command byte, then data:
    ///
    ///   "16 40 01;"            NR on            (16 40 = noise reduction)
    ///   "16 40 01;16 22 01;"   NR on + NB on    (';' chains commands)
    ///   "0700;07A0;"           copy A to B      (spaces are optional)
    ///
    /// What's <i>not</i> here is the framing: the FE FE / address / FD wrapper
    /// is added by <see cref="CivProtocol.BuildFrame"/> inside
    /// CivRadioController, so a macro can never forge an address or a frame
    /// boundary — it only ever chooses which command the app sends to the
    /// radio it is already talking to.
    /// </summary>
    public static class CivMacroCodec
    {
        /// <summary>
        /// Longest command body a macro may carry. The longest thing the app
        /// itself sends is a memory-channel write (~50 bytes); macros are for
        /// short control commands, and the cap keeps a typo from becoming a
        /// long burst on the bus.
        /// </summary>
        public const int MaxCommandBytes = 16;

        /// <summary>
        /// Split <paramref name="text"/> on ';' and decode each segment's hex
        /// into a CI-V command body. Whitespace inside a segment is ignored, so
        /// "16 40 01" and "164001" are the same command. Returns false with a
        /// user-facing <paramref name="error"/> (no exception) if any segment is
        /// malformed — the Settings validator shows it verbatim.
        /// </summary>
        public static bool TryParse(string? text, out List<byte[]> commands, out string error)
        {
            commands = new List<byte[]>();
            error = "";

            if (string.IsNullOrWhiteSpace(text))
            {
                error = "no CI-V command given";
                return false;
            }

            foreach (var rawSegment in text.Split(';', StringSplitOptions.RemoveEmptyEntries))
            {
                var segment = rawSegment.Replace(" ", "").Replace("\t", "");
                if (segment.Length == 0) continue;              // "16 40 01; ;" — stray separator

                if (segment.Length % 2 != 0)
                {
                    error = $"'{rawSegment.Trim()}' has an odd number of hex digits — CI-V is whole bytes, e.g. \"16 40 01\"";
                    return false;
                }
                if (segment.Length / 2 > MaxCommandBytes)
                {
                    error = $"'{rawSegment.Trim()}' is longer than {MaxCommandBytes} bytes";
                    return false;
                }

                var body = new byte[segment.Length / 2];
                for (int i = 0; i < body.Length; i++)
                {
                    if (!byte.TryParse(segment.AsSpan(i * 2, 2), NumberStyles.HexNumber,
                                       CultureInfo.InvariantCulture, out body[i]))
                    {
                        error = $"'{rawSegment.Trim()}' isn't hex — expected pairs of 0-9 A-F, e.g. \"16 40 01\"";
                        return false;
                    }
                }
                commands.Add(body);
            }

            if (commands.Count == 0)
            {
                error = "no CI-V command given";
                return false;
            }
            return true;
        }

        /// <summary>Render a command body back as spaced hex for logs ("16 40 01").</summary>
        public static string Describe(byte[] command)
            => string.Join(' ', Array.ConvertAll(command, b => b.ToString("X2", CultureInfo.InvariantCulture)));
    }
}
