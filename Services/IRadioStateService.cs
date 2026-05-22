namespace Yaesu_Web_Control.Services
{
    public interface IRadioStateService
    {
        string Id { get; set; }
        int? AGCMain { get; set; }
        int? AGCSub { get; set; }
        int? RFMain { get; set; }
        int? RFSub { get; set; }
        long FrequencyA { get; set; }
        long FrequencyB { get; set; }
        string? FR { get; set; }
        string? FT { get; set; }
        string? ModeA { get; set; }
        string? ModeB { get; set; }
        int? SMeterA { get; set; }
        int? SMeterB { get; set; }
        string? AntennaA { get; set; }
        string? AntennaB { get; set; }
        int Power { get; set; }
        bool IsTransmitting { get; set; }
        // Add more properties as needed for your app

       
    }
}