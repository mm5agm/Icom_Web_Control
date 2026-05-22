namespace Yaesu_Web_Control.Services.Sdr
{
    /// <summary>
    /// Describes a SoapySDR device found during enumeration.
    /// Key is the full SoapySDR args string (e.g. "driver=rtlsdr,serial=00000001") and
    /// is used as the stable identifier stored in ApplicationSettings.SdrDeviceKey.
    /// </summary>
    public record SdrDeviceInfo(string Key, string Label, string Driver);
}
