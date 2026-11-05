using System.IO.Ports;
using Microsoft.Win32;
using RadioWebControl.Core.Services.Cw;
using Icom_Web_Control.Services.Civ;

namespace Icom_Web_Control.Services.Cw
{
    /// <summary>A key line on a COM port, keyed by DTR.</summary>
    public interface IPortCwKeyLine : ICwKeyLine, IDisposable
    {
        string PortName { get; }
    }

    /// <summary>
    /// Keys the radio by DTR on a COM port of its own: the IC-7300MK2's
    /// second USB port, when its USB Keying (CW) menu is set to DTR on that
    /// port. The port opens with DTR and RTS low, and is set low again straight
    /// after opening in case the driver raised it. Closing sets DTR low first.
    /// </summary>
    public sealed class SerialCwKeyLine : IPortCwKeyLine
    {
        private readonly SerialPort _port;

        public SerialCwKeyLine(string portName)
        {
            _port = new SerialPort(portName)
            {
                Handshake = Handshake.None,
                DtrEnable = false,
                RtsEnable = false,
            };
            _port.Open();
            _port.DtrEnable = false;
            _port.RtsEnable = false;
        }

        public string PortName => _port.PortName;

        /// <summary>Throws if the port has gone (cable pulled); the service lets go and drops back to the dry run.</summary>
        public void SetKey(bool down) => _port.DtrEnable = down;

        public void Dispose()
        {
            try { _port.DtrEnable = false; } catch { /* the port may already be gone */ }
            _port.Dispose();
        }

        /// <summary>
        /// The serial ports, each with the registry's name where it has one
        /// (e.g. "USB Serial Port (COM7)"), so the page can say which is which.
        /// </summary>
        public static List<(string Name, string Description)> Ports()
        {
            // None to offer until keying is proven off Windows; the dry run still works.
            if (!OperatingSystem.IsWindows()) return new();

            var names = SerialPort.GetPortNames().Distinct(StringComparer.OrdinalIgnoreCase).ToList();
            var described = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            try
            {
                using var usb = Registry.LocalMachine.OpenSubKey(@"SYSTEM\CurrentControlSet\Enum\USB");
                foreach (var device in usb?.GetSubKeyNames() ?? Array.Empty<string>())
                {
                    using var dev = usb!.OpenSubKey(device);
                    foreach (var instance in dev?.GetSubKeyNames() ?? Array.Empty<string>())
                    {
                        using var inst = dev!.OpenSubKey(instance);
                        using var parms = inst?.OpenSubKey("Device Parameters");
                        if (parms?.GetValue("PortName") is string port && inst?.GetValue("FriendlyName") is string friendly)
                            described[port] = friendly;
                    }
                }
            }
            catch
            {
                // Names are a help, not a need: list the ports without them.
            }
            return names
                .OrderBy(n => n.TrimEnd("0123456789".ToCharArray()), StringComparer.Ordinal)
                .ThenBy(n => int.TryParse(n.AsSpan(n.TrimEnd("0123456789".ToCharArray()).Length), out int k) ? k : -1)
                .Select(n => (n, described.TryGetValue(n, out var d) ? d : n))
                .ToList();
        }
    }

    /// <summary>
    /// Keys the radio by DTR on the CI-V port IWC already holds open: the
    /// original IC-7300 has one USB port, which carries CI-V and, with USB
    /// Keying (CW) set to DTR, the key as well. Windows lets only one program
    /// open a port, so the key has to go through IWC's own handle. Closing
    /// drops DTR; if the CI-V port closes, the next key throws and the service
    /// goes back to the dry run.
    /// </summary>
    public sealed class CivDtrCwKeyLine : IPortCwKeyLine
    {
        private readonly ICivClient _civ;

        public CivDtrCwKeyLine(ICivClient civ, string portName)
        {
            _civ = civ;
            PortName = portName;
            _civ.SetDtr(false);
        }

        public string PortName { get; }

        public void SetKey(bool down)
        {
            if (!string.Equals(_civ.PortName, PortName, StringComparison.OrdinalIgnoreCase))
                throw new InvalidOperationException($"the CI-V port {PortName} is no longer open");
            _civ.SetDtr(down);
        }

        public void Dispose()
        {
            try
            {
                if (string.Equals(_civ.PortName, PortName, StringComparison.OrdinalIgnoreCase)) _civ.SetDtr(false);
            }
            catch { /* the port may already be gone */ }
        }
    }
}
