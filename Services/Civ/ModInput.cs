using System.Threading;

namespace Icom_Web_Control.Services.Civ
{
    /// <summary>
    /// The MOD input: where transmit audio comes from, DATA OFF MOD outside the
    /// data modes and DATA MOD in them (IWC #144). Both are SET-menu items read
    /// and written with 1A 05; which items and which choices come from the
    /// radio's profile. Choosing a source never keys the radio.
    /// </summary>
    public static class ModInput
    {
        /// <summary>What the radio has now. A value is null when it couldn't be read.</summary>
        public sealed record State(int? DataOff, int? Data);

        public static async Task<State> ReadAsync(IRadioController radio, ModInputItems items, CancellationToken ct = default)
            => new(Decode(await radio.ReadMenuItemAsync(items.DataOffItem, ct), items),
                   Decode(await radio.ReadMenuItemAsync(items.DataItem, ct), items));

        /// <summary>
        /// Set DATA MOD (<paramref name="dataMode"/> true) or DATA OFF MOD to
        /// <paramref name="choice"/>, then read it back. Null when the choice is
        /// out of range or the radio didn't take it.
        /// </summary>
        public static async Task<int?> SetAsync(IRadioController radio, ModInputItems items, bool dataMode, int choice, CancellationToken ct = default)
        {
            if (choice < 0 || choice >= items.Choices.Count) return null;
            int item = dataMode ? items.DataItem : items.DataOffItem;
            if (await radio.WriteMenuItemAsync(item, new[] { (byte)(((choice / 10) << 4) | (choice % 10)) }, ct) != true)
                return null;
            return Decode(await radio.ReadMenuItemAsync(item, ct), items);
        }

        /// <summary>One BCD byte that names a choice; anything else is null.</summary>
        public static int? Decode(byte[]? data, ModInputItems items)
        {
            if (data is not { Length: 1 }) return null;
            if ((data[0] & 0x0F) > 9 || (data[0] >> 4) > 9) return null;
            int v = CivProtocol.BcdByte(data[0]);
            return v < items.Choices.Count ? v : null;
        }
    }
}
