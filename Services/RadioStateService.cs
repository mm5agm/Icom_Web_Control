using Icom_Web_Control.Hubs;
using Microsoft.AspNetCore.SignalR;
using System.ComponentModel;
using System.Runtime.CompilerServices;
using static System.Runtime.InteropServices.JavaScript.JSType;

namespace Icom_Web_Control.Services
{
    public class RadioStateService : INotifyPropertyChanged, IRadioStateService
    {
        private readonly ILogger<RadioStateService> _logger;
        private readonly IHubContext<RadioHub> _hubContext;
        private readonly RadioStatePersistenceService _statePersistence;
        private readonly IBandPlanService _bandPlan;

        public bool IsInitialized { get; set; } = false;

        // True when the configured radio model has a single physical receiver.
        // Both IC-7300 models do, so this is true throughout; it is set at
        // startup from RadioCapabilities so the assumption lives in one place.
        // IntentDispatcher uses it to route a per-VFO control onto whichever
        // VFO is currently active, rather than always writing to *A state —
        // on one receiver the targeted panel is a hint, not an address.
        public bool IsSingleReceiver { get; set; } = false;

        // Configured radio model — "IC-7300" or "IC-7300MK2". Set at startup;
        // the two differ in CI-V default address (94 vs B6) rather than in
        // anything this class has to normalise.
        public string RadioModel { get; set; } = "";

        private RadioState _initialState;

        public RadioStateService(
            ILogger<RadioStateService> logger,
            RadioStatePersistenceService statePersistence,
            IHubContext<RadioHub> hubContext,
            IBandPlanService bandPlan)
        {
            _logger = logger;
            _statePersistence = statePersistence;
            _hubContext = hubContext;
            _bandPlan = bandPlan;
            _initialState = _statePersistence.Load();

            _logger.LogInformation("RadioStateService constructed with IHubContext: {HubContextAvailable}", hubContext != null);

            // ADD THIS LOG:
            _logger.LogInformation("RadioStateService constructed with initial state: ModeA={ModeA}, ModeB={ModeB}, Power={Power}, AntennaA={AntennaA}, AntennaB={AntennaB}, MicGain={MicGain}",
                _initialState.ModeA, _initialState.ModeB, _initialState.Power, _initialState.AntennaA, _initialState.AntennaB, _initialState.MicGain);

            // Initialize properties from _initialState
            FrequencyA = _initialState.FrequencyA;
            FrequencyB = _initialState.FrequencyB;
            BandA = _initialState.BandA;
            BandB = _initialState.BandB;
            ModeA = _initialState.ModeA ?? "";
            ModeB = _initialState.ModeB ?? "";
            AntennaA = _initialState.AntennaA ?? "";
            AntennaB = _initialState.AntennaB ?? "";
            RoofingFilterA = _initialState.RoofingFilterA ?? "";
            RoofingFilterB = _initialState.RoofingFilterB ?? "";
            Power = _initialState.Power;
            MicGain = _initialState.MicGain;
            ProcEnabled = _initialState.ProcEnabled;
            ProcLevel = _initialState.ProcLevel;
            IfWidthA = _initialState.IfWidthA ?? "";
            IfWidthB = _initialState.IfWidthB ?? "";
            SelectedFilterA = _initialState.SelectedFilterA ?? "";
            SelectedFilterB = _initialState.SelectedFilterB ?? "";
            IfShiftA = _initialState.IfShiftA;
            IfShiftB = _initialState.IfShiftB;
            AfGainA = _initialState.AfGainA;
            AfGainB = _initialState.AfGainB;
        }

        public RadioState InitialState => _initialState;

        private void SetField<T>(ref T field, T value, [CallerMemberName] string? propertyName = null)
        {
            if (!EqualityComparer<T>.Default.Equals(field, value))
            {
                // Debug, not Information: SetField runs on every CAT response
                // (dozens per second during the init burst and AI streaming).
                // At Information these three lines per change were a primary
                // source of the synchronous-logging flood that starved the
                // thread pool during startup (issue #73).
                _logger.LogDebug("[SetField] Setting {Property} from {OldValue} to {NewValue}", propertyName, field, value);
                field = value;
                OnPropertyChanged(propertyName);
                BroadcastUpdate(propertyName!, value!);

                // Only save after initialization is complete
                if (IsInitialized)
                {
                    _logger.LogDebug("[SetField] Persisting state (IsInitialized=true, {Property}={Value})", propertyName, value);
                    _statePersistence.Save(this.ToRadioState());
                }
                else
                {
                    _logger.LogDebug("[SetField] NOT persisting {Property} (IsInitialized=false)", propertyName);
                }
            }
            else
            {
                // Log when value doesn't change (helpful for debugging band issues)
                if (propertyName == "BandA" || propertyName == "BandB")
                {
                    _logger.LogDebug("[SetField] {Property} unchanged (already {Value})", propertyName, value);
                }
            }
        }

        // Call this after DT0; is received -- marks the fast-init burst phase
        // complete and lets subsequent SetField calls persist to disk.
        //
        // Historically this also called ReloadFromPersistence() with the
        // comment "Load the latest persisted state into memory". That was
        // actively harmful: the fast-init burst (AI1; + InitializationCommands
        // + DT0;) is the FIRST chance to read the radio's actual state, and
        // those responses had already arrived and populated RadioStateService
        // by the time DT0 came back. Reloading from disk at that moment
        // overwrote every fresh radio value with stale last-session values.
        // RadioInitializationService.readQueries then re-read most of them,
        // but anything where the response timed out (150 ms) silently kept
        // the stale value -- which is why Jacek SP3L's #40-#46 still showed
        // wrong values in v2.3.9-pre1 even after all the other fixes:
        // every property in those reports is one where the persisted-state
        // overwrite won the race for at least some users.
        //
        // The radio is the source of truth on connect. Persisted state was
        // already loaded in the constructor as a fallback for properties the
        // radio does not report (or hasn't reported yet). Once init starts,
        // we should never reload it.
        public void CompleteInitialization()
        {
            IsInitialized = true;
            // Do NOT call Save() here!
        }

        private void BroadcastUpdate(string property, object value)
        {
            _logger.LogDebug("[BroadcastUpdate] Broadcasting {Property} = {Value}", property, value);
            // Special case: PowerMeter should include isTransmitting for frontend sync
            if (property == "PowerMeter")
            {
                _hubContext.Clients.All.SendAsync("RadioStateUpdate", new { property, value = new { value, isTransmitting = this.IsTransmitting } });
            }
            else
            {
                _hubContext.Clients.All.SendAsync("RadioStateUpdate", new { property, value });
            }
        }

        // --- Properties for all CAT commands in GetInitialValues() ---

        private string _id = "";
        public string Id { get => _id; set => SetField(ref _id, value); }

        private int? _agcMain;
        public int? AGCMain { get => _agcMain; set => SetField(ref _agcMain, value); }
        private int? _agcSub;
        public int? AGCSub { get => _agcSub; set => SetField(ref _agcSub, value); }

        // GT command AGC select: "0"=OFF "1"=FAST "2"=MID "3"=SLOW "4"=AUTO
        // Values 5/6 (AUTO-FAST/MID/SLOW) are read-only radio states, mapped to "4" in the UI.
        private string _agcA = "2";
        public string AgcA { get => _agcA; set => SetField(ref _agcA, value); }
        private string _agcB = "2";
        public string AgcB { get => _agcB; set => SetField(ref _agcB, value); }

        // PA command IPO/AMP: "0"=IPO "1"=AMP1 "2"=AMP2
        private string _ipoA = "0";
        public string IpoA { get => _ipoA; set => SetField(ref _ipoA, value); }
        private string _ipoB = "0";
        public string IpoB { get => _ipoB; set => SetField(ref _ipoB, value); }

        // BC command Auto Notch: "0"=OFF "1"=ON
        private string _autoNotchA = "0";
        public string AutoNotchA { get => _autoNotchA; set => SetField(ref _autoNotchA, value); }
        private string _autoNotchB = "0";
        public string AutoNotchB { get => _autoNotchB; set => SetField(ref _autoNotchB, value); }

        // NR command Noise Reduction: "0"=OFF "1"=NR1 "2"=NR2
        private string _nrA = "0";
        public string NrA { get => _nrA; set => SetField(ref _nrA, value); }
        private string _nrB = "0";
        public string NrB { get => _nrB; set => SetField(ref _nrB, value); }

        // RA command Attenuator: "00"=OFF "06"=6dB "12"=12dB "18"=18dB
        private string _attA = "00";
        public string AttA { get => _attA; set => SetField(ref _attA, value); }
        private string _attB = "00";
        public string AttB { get => _attB; set => SetField(ref _attB, value); }

        // BP command Manual Notch on/off: "0"=OFF "1"=ON
        private string _manualNotchA = "0";
        public string ManualNotchA { get => _manualNotchA; set => SetField(ref _manualNotchA, value); }
        private string _manualNotchB = "0";
        public string ManualNotchB { get => _manualNotchB; set => SetField(ref _manualNotchB, value); }
        // BP command Manual Notch frequency: 10–3200 Hz (CAT value = Hz ÷ 10, 3 digits)
        private int _manualNotchFreqA = 1000;
        public int ManualNotchFreqA { get => _manualNotchFreqA; set => SetField(ref _manualNotchFreqA, value); }
        private int _manualNotchFreqB = 1000;
        public int ManualNotchFreqB { get => _manualNotchFreqB; set => SetField(ref _manualNotchFreqB, value); }
        // Manual-notch filter width (CI-V 16 57): "0"=WIDE "1"=MID "2"=NAR
        private string _manualNotchWidthA = "1";
        public string ManualNotchWidthA { get => _manualNotchWidthA; set => SetField(ref _manualNotchWidthA, value); }
        private string _manualNotchWidthB = "1";
        public string ManualNotchWidthB { get => _manualNotchWidthB; set => SetField(ref _manualNotchWidthB, value); }
        // IF DSP filter shape (CI-V 16 56): "0"=SHARP "1"=SOFT
        private string _ifShapeA = "0";
        public string IfShapeA { get => _ifShapeA; set => SetField(ref _ifShapeA, value); }
        private string _ifShapeB = "0";
        public string IfShapeB { get => _ifShapeB; set => SetField(ref _ifShapeB, value); }

        private int? _rfMain;
        public int? RFMain { get => _rfMain; set => SetField(ref _rfMain, value); }
        private int? _rfSub;
        public int? RFSub { get => _rfSub; set => SetField(ref _rfSub, value); }

        private long _frequencyA;
        public long FrequencyA
        {
            get => _frequencyA;
            set
            {
                SetField(ref _frequencyA, value);
                UpdateBandFromFrequency();
            }
        }

        private long _frequencyB;
        public long FrequencyB
        {
            get => _frequencyB;
            set
            {
                SetField(ref _frequencyB, value);
                UpdateBandFromFrequency();
            }
        }

        private string? _fr;
        public string? FR { get => _fr; set => SetField(ref _fr, value); }
        private string? _ft;
        public string? FT { get => _ft; set => SetField(ref _ft, value); }

        private string? _ss04;
        public string? SS04 { get => _ss04; set => SetField(ref _ss04, value); }
        private string? _ss14;
        public string? SS14 { get => _ss14; set => SetField(ref _ss14, value); }

        private string? _ao;
        public string? AO { get => _ao; set => SetField(ref _ao, value); }
        private string? _mg;
        public string? MG { get => _mg; set => SetField(ref _mg, value); }
        private string? _pl;
        public string? PL { get => _pl; set => SetField(ref _pl, value); }
        private string? _pr0;
        public string? PR0 { get => _pr0; set => SetField(ref _pr0, value); }
        private string? _pr1;
        public string? PR1 { get => _pr1; set => SetField(ref _pr1, value); }
        private string? _md0;
        public string? MD0 { get => _md0; set => SetField(ref _md0, value); }
        private string? _md1;
        public string? MD1 { get => _md1; set => SetField(ref _md1, value); }
        private string? _vs;
        public string? VS { get => _vs; set => SetField(ref _vs, value); }
        private string? _kp;
        public string? KP { get => _kp; set => SetField(ref _kp, value); }
        private string? _pc;
        public string? PC { get => _pc; set => SetField(ref _pc, value); }
        private string? _rl0;
        public string? RL0 { get => _rl0; set => SetField(ref _rl0, value); }
        private string? _rl1;
        public string? RL1 { get => _rl1; set => SetField(ref _rl1, value); }
        private string? _nr0;
        public string? NR0 { get => _nr0; set => SetField(ref _nr0, value); }
        private string? _nr1;
        public string? NR1 { get => _nr1; set => SetField(ref _nr1, value); }
        private string? _nb0;
        public string? NB0 { get => _nb0; set => SetField(ref _nb0, value); }
        private string? _nb1;
        public string? NB1 { get => _nb1; set => SetField(ref _nb1, value); }
        private string? _nl0;
        public string? NL0 { get => _nl0; set => SetField(ref _nl0, value); }

        private string _nbA = "0";
        public string NbA { get => _nbA; set => SetField(ref _nbA, value); }
        private string _nbB = "0";
        public string NbB { get => _nbB; set => SetField(ref _nbB, value); }

        // IC-7300 IF passband filter width, in Hz as a string (CI-V 1A 03).
        // "" = unknown / mode has no adjustable width (FM).
        private string _ifWidthA = "";
        public string IfWidthA { get => _ifWidthA; set => SetField(ref _ifWidthA, value); }
        private string _ifWidthB = "";
        public string IfWidthB { get => _ifWidthB; set => SetField(ref _ifWidthB, value); }

        // Selected IF filter slot: "1"=FIL1 "2"=FIL2 "3"=FIL3, "" = unknown.
        private string _selectedFilterA = "";
        public string SelectedFilterA { get => _selectedFilterA; set => SetField(ref _selectedFilterA, value); }
        private string _selectedFilterB = "";
        public string SelectedFilterB { get => _selectedFilterB; set => SetField(ref _selectedFilterB, value); }

        // IS command IF Shift: stored in Hz, -1000 to +1000. CAT encodes as 0000-9999 (center=5000=0Hz).
        private int _ifShiftA = 0;
        public int IfShiftA { get => _ifShiftA; set => SetField(ref _ifShiftA, value); }
        private int _ifShiftB = 0;
        public int IfShiftB { get => _ifShiftB; set => SetField(ref _ifShiftB, value); }

        private int _clarifierOffsetA = 0;
        public int ClarifierOffsetA { get => _clarifierOffsetA; set => SetField(ref _clarifierOffsetA, value); }
        private int _clarifierOffsetB = 0;
        public int ClarifierOffsetB { get => _clarifierOffsetB; set => SetField(ref _clarifierOffsetB, value); }
        private bool _rxClarOn = false;
        public bool RxClarOn { get => _rxClarOn; set => SetField(ref _rxClarOn, value); }
        private bool _txClarOn = false;
        public bool TxClarOn { get => _txClarOn; set => SetField(ref _txClarOn, value); }

        private bool _apfOnA = false;
        public bool ApfOnA { get => _apfOnA; set => SetField(ref _apfOnA, value); }
        private bool _apfOnB = false;
        public bool ApfOnB { get => _apfOnB; set => SetField(ref _apfOnB, value); }
        private int _apfWidthA = 0;
        public int ApfWidthA { get => _apfWidthA; set => SetField(ref _apfWidthA, value); }
        private int _apfWidthB = 0;
        public int ApfWidthB { get => _apfWidthB; set => SetField(ref _apfWidthB, value); }

        private bool _isConnected = false;
        public bool IsConnected { get => _isConnected; set => SetField(ref _isConnected, value); }

        // Human-readable reason the link isn't up, shown as a banner on the home
        // screen (empty string = nothing to show). Set by CivRadioController's
        // connect attempt to distinguish "configured serial port not found" (a
        // config/cabling problem the user can act on) from "port present but
        // radio silent" (radio off). Broadcasts like any other property.
        private string _connectionStatusText = "";
        public string ConnectionStatusText { get => _connectionStatusText; set => SetField(ref _connectionStatusText, value ?? ""); }

        private string _bandA = "20m";
        public string BandA { get => _bandA; set => SetField(ref _bandA, value); }

        private string _bandB = "20m";
        public string BandB { get => _bandB; set => SetField(ref _bandB, value); }

        public Dictionary<string, object> Controls { get; } = new();

        public void SetBand(string receiver, string band)
        {
            if (receiver == "A")
                BandA = band;
            else if (receiver == "B")
                BandB = band;
        }

        public void SetAntenna(string receiver, string antenna)
        {
            if (receiver == "A")
                AntennaA = antenna;
            else if (receiver == "B")
                AntennaB = antenna;
        }

        private int _power;
        public int Power { get => _power; set => SetField(ref _power, value); }

        private string? _modeA = "";
        public string? ModeA { get => _modeA; set => SetField(ref _modeA, value); }

        private string? _modeB = "";
        public string? ModeB { get => _modeB; set => SetField(ref _modeB, value); }

        private string? _antennaA = "";
        public string? AntennaA { get => _antennaA; set => SetField(ref _antennaA, value); }

        private string? _antennaB = "";
        public string? AntennaB { get => _antennaB; set => SetField(ref _antennaB, value); }

        private string _roofingFilterA = "";
        public string RoofingFilterA { get => _roofingFilterA; set => SetField(ref _roofingFilterA, value); }

        private string _roofingFilterB = "";
        public string RoofingFilterB { get => _roofingFilterB; set => SetField(ref _roofingFilterB, value); }


        private int? _sMeterA;
        public int? SMeterA
        {
            get => _sMeterA;
            set
            {
                if (value == null) return;
                int clamped = Math.Clamp(value.Value, 0, 255);
                // Spike filtering removed for full responsiveness
                SetField(ref _sMeterA, clamped);
            }
        }

        private int? _sMeterB;
        public int? SMeterB
        {
            get => _sMeterB;
            set
            {
                if (value == null) return;
                int clamped = Math.Clamp(value.Value, 0, 255);
                // Spike filtering removed for full responsiveness
                SetField(ref _sMeterB, clamped);
            }
        }

        private int? _powerMeter;
        public int? PowerMeter { get => _powerMeter; set => SetField(ref _powerMeter, value); }

        private int? _compressionMeter;
        public int? CompressionMeter
        {
            get => _compressionMeter;
            set
            {
                if (value == null) return;
                int clamped = Math.Clamp(value.Value, 0, 255);
                SetField(ref _compressionMeter, clamped);
            }
        }

        private int? _alcMeter;
        public int? ALCMeter
        {
            get => _alcMeter;
            set
            {
                if (value == null) return;
                int clamped = Math.Clamp(value.Value, 0, 255);
                SetField(ref _alcMeter, clamped);
            }
        }

        private int? _swrMeter;
        public int? SWRMeter
        {
            get => _swrMeter;
            set
            {
                if (value == null) return;
                int clamped = Math.Clamp(value.Value, 0, 255);
                if (_swrMeter.HasValue && _swrMeter.Value != 0 && clamped != 0 && Math.Abs(clamped - _swrMeter.Value) > 30)
                {
                    _logger.LogWarning("[SWRMeter] Ignored spike: {Old} -> {New}", _swrMeter, clamped);
                    return;
                }
                SetField(ref _swrMeter, clamped);
            }
        }

        // Removed duplicate int? _power and int? Power definitions. Only int _power and int Power property remain.

        private int? _maxPower;
        public int? MaxPower
        {
            get => _maxPower;
            set => SetField(ref _maxPower, value);
        }

        private bool _isTransmitting;
        public bool IsTransmitting { get => _isTransmitting; set => SetField(ref _isTransmitting, value); }

        private int? _iddMeter;
        public int? IDDMeter { get => _iddMeter; set => SetField(ref _iddMeter, value); }

        private int? _vddMeter;
        public int? VDDMeter { get => _vddMeter; set => SetField(ref _vddMeter, value); }

        private int? _temperature;
        public int? Temperature { get => _temperature; set => SetField(ref _temperature, value); }

        private int _afGainA;
        public int AfGainA { get => _afGainA; set => SetField(ref _afGainA, value); }
        private int _afGainB;
        public int AfGainB { get => _afGainB; set => SetField(ref _afGainB, value); }

        private int _micGain = 50;
        public int MicGain { get => _micGain; set => SetField(ref _micGain, value); }

        private bool _procEnabled = false;
        public bool ProcEnabled { get => _procEnabled; set => SetField(ref _procEnabled, value); }

        private int _procLevel = 50;
        public int ProcLevel { get => _procLevel; set => SetField(ref _procLevel, value); }

        // ATU: false = bypass, true = engaged
        private bool _atuEnabled = false;
        public bool AtuEnabled { get => _atuEnabled; set => SetField(ref _atuEnabled, value); }

        // ATU auto-tune cycle currently in progress. Driven by P2 of the AC
        // command's answer/auto-info — radio sets P2=1 while the matching
        // network is being adjusted, P2=0 when finished. The UI uses this to
        // grey/animate the ATU button while a tune is running.
        private bool _atuTuning = false;
        public bool AtuTuning { get => _atuTuning; set => SetField(ref _atuTuning, value); }

        // NB Level per VFO: 1–20
        private int _nbLevelA = 10;
        public int NbLevelA { get => _nbLevelA; set => SetField(ref _nbLevelA, value); }
        private int _nbLevelB = 10;
        public int NbLevelB { get => _nbLevelB; set => SetField(ref _nbLevelB, value); }

        // NR Level (RL command) per VFO: 1–15.
        // On FTdx10 / FT-710 this is the DNR algorithm selector (Jacek
        // SP3L #47 -- the FTdx10 has no NR1/NR2 distinction, only ON/OFF
        // plus this 1–15 algorithm number, semantically like "NB Level").
        // On FTdx101 this is the level that applies to whichever NR type
        // (NR1 or NR2) is currently selected.
        private int _nrLevelA = 1;
        public int NrLevelA { get => _nrLevelA; set => SetField(ref _nrLevelA, value); }
        private int _nrLevelB = 1;
        public int NrLevelB { get => _nrLevelB; set => SetField(ref _nrLevelB, value); }

        // CW Pitch: IC-7300 sidetone/pitch in Hz, 300–900 (CI-V 14 09)
        private int _cwPitch = 600; // default 600 Hz
        public int CwPitch { get => _cwPitch; set => SetField(ref _cwPitch, value); }

        // RF Gain per VFO: 0–255 (RG command)
        private int _rfGainA = 255;
        public int RfGainA { get => _rfGainA; set => SetField(ref _rfGainA, value); }
        private int _rfGainB = 255;
        public int RfGainB { get => _rfGainB; set => SetField(ref _rfGainB, value); }

        // Squelch per VFO: 0–255 (SQ command)
        private int _squelchA = 0;
        public int SquelchA { get => _squelchA; set => SetField(ref _squelchA, value); }
        private int _squelchB = 0;
        public int SquelchB { get => _squelchB; set => SetField(ref _squelchB, value); }

        // Monitor/sidetone on/off and level (ML command)
        private bool _monitorOn = false;
        public bool MonitorOn { get => _monitorOn; set => SetField(ref _monitorOn, value); }
        private int _monitorLevelA = 0;
        public int MonitorLevelA { get => _monitorLevelA; set => SetField(ref _monitorLevelA, value); }
        private int _monitorLevelB = 0;
        public int MonitorLevelB { get => _monitorLevelB; set => SetField(ref _monitorLevelB, value); }

        // VOX
        private bool _voxOn = false;
        public bool VoxOn { get => _voxOn; set => SetField(ref _voxOn, value); }
        private int _voxGain = 50;
        public int VoxGain { get => _voxGain; set => SetField(ref _voxGain, value); }
        private int _voxDelay = 50;
        public int VoxDelay { get => _voxDelay; set => SetField(ref _voxDelay, value); }
        private int _antiVoxGain = 50;
        public int AntiVoxGain { get => _antiVoxGain; set => SetField(ref _antiVoxGain, value); }

        // FM Repeater
        private string _fmShiftDir = "0";
        public string FmShiftDir { get => _fmShiftDir; set => SetField(ref _fmShiftDir, value); }
        private int _fmOffsetHz = 600000;
        public int FmOffsetHz { get => _fmOffsetHz; set => SetField(ref _fmOffsetHz, value); }
        private string _ctcssMode = "00";
        public string CtcssMode { get => _ctcssMode; set => SetField(ref _ctcssMode, value); }
        // Tenths of a Hz, as CI-V 1B carries it: "885" is 88.5 Hz.
        private string _ctcssTone = "885";
        public string CtcssTone { get => _ctcssTone; set => SetField(ref _ctcssTone, value); }

        // CW Keyer. Speed in WPM (6–48); break-in mode "0"/"1"/"2"; break-in
        // delay stored as dots×10 (IC-7300 unit is dots, 2.0–13.0 → 20–130).
        private int _cwSpeed = 20;
        public int CwSpeed { get => _cwSpeed; set => SetField(ref _cwSpeed, value); }
        private string _cwBreakIn = "0";
        public string CwBreakIn { get => _cwBreakIn; set => SetField(ref _cwBreakIn, value); }
        private int _cwBreakInDelay = 30; // dots×10 → 3.0 dots
        public int CwBreakInDelay { get => _cwBreakInDelay; set => SetField(ref _cwBreakInDelay, value); }

        private bool _radioPowerOn = true; // Assume on when app starts
        public bool RadioPowerOn { get => _radioPowerOn; set => SetField(ref _radioPowerOn, value); }

        // TX VFO: 0 = VFO A is TX, 1 = VFO B is TX
        private int _txVfo = 0;
        public int TxVfo { get => _txVfo; set => SetField(ref _txVfo, value); }

        // VS command — VFO SELECT, indicates which VFO is currently the
        // operating (RX) VFO. Distinct from TxVfo (FT) which only tracks
        // the TX VFO in split mode. On single-receiver radios the front-
        // panel A/B button changes ActiveVfo but does NOT change TxVfo
        // (Jacek SP3L #34 R2 — fixed by switching normal-mode greying
        // from TxVfo to ActiveVfo). 0 = VFO A, 1 = VFO B.
        private int _activeVfo = 0;
        public int ActiveVfo { get => _activeVfo; set => SetField(ref _activeVfo, value); }

        // Split mode: 0 = OFF, 1 = ON (VFO A = RX, VFO B = TX), 2 = ON + Quick Split (+5 kHz)
        private int _splitMode = 0;
        public int SplitMode { get => _splitMode; set => SetField(ref _splitMode, value); }

        // Snapshot of every UI-relevant state property, replayed to each newly
        // connected SignalR client by RadioHub.OnConnectedAsync through the
        // normal RadioStateUpdate envelope. Broadcasts only fire on *change*,
        // so a browser that connects after initialization (second tab, another
        // computer) would otherwise keep the frontend's JS defaults for any
        // property not server-rendered in Index.cshtml — most visibly
        // ActiveVfo/TxVfo/SplitMode, which made VFO A always look active on
        // late-joining clients. Property names must match the handlers in
        // wwwroot/js/ui/site.js. Meters are excluded (they stream at ~10 Hz).
        public IReadOnlyList<KeyValuePair<string, object>> GetClientStateSnapshot()
        {
            return new List<KeyValuePair<string, object>>
            {
                new("IsConnected", IsConnected),
                new("ConnectionStatusText", ConnectionStatusText ?? ""),
                new("RadioPowerOn", RadioPowerOn),
                new("FrequencyA", FrequencyA),
                new("FrequencyB", FrequencyB),
                new("BandA", BandA),
                new("BandB", BandB),
                new("ModeA", ModeA ?? ""),
                new("ModeB", ModeB ?? ""),
                new("AntennaA", AntennaA ?? ""),
                new("AntennaB", AntennaB ?? ""),
                new("ActiveVfo", ActiveVfo),
                new("TxVfo", TxVfo),
                new("SplitMode", SplitMode),
                new("IsTransmitting", IsTransmitting),
                new("Power", Power),
                new("RoofingFilterA", RoofingFilterA ?? ""),
                new("RoofingFilterB", RoofingFilterB ?? ""),
                new("AgcA", AgcA),
                new("AgcB", AgcB),
                new("IpoA", IpoA),
                new("IpoB", IpoB),
                new("AttA", AttA),
                new("AttB", AttB),
                new("NrA", NrA),
                new("NrB", NrB),
                new("NrLevelA", NrLevelA),
                new("NrLevelB", NrLevelB),
                new("NbA", NbA),
                new("NbB", NbB),
                new("NbLevelA", NbLevelA),
                new("NbLevelB", NbLevelB),
                new("AutoNotchA", AutoNotchA),
                new("AutoNotchB", AutoNotchB),
                new("ManualNotchA", ManualNotchA),
                new("ManualNotchB", ManualNotchB),
                new("ManualNotchFreqA", ManualNotchFreqA),
                new("ManualNotchFreqB", ManualNotchFreqB),
                new("ManualNotchWidthA", ManualNotchWidthA),
                new("ManualNotchWidthB", ManualNotchWidthB),
                new("IfShapeA", IfShapeA),
                new("IfShapeB", IfShapeB),
                new("IfWidthA", IfWidthA),
                new("IfWidthB", IfWidthB),
                new("SelectedFilterA", SelectedFilterA),
                new("SelectedFilterB", SelectedFilterB),
                new("IfShiftA", IfShiftA),
                new("IfShiftB", IfShiftB),
                new("RxClarOn", RxClarOn),
                new("TxClarOn", TxClarOn),
                new("ClarifierOffsetA", ClarifierOffsetA),
                new("ClarifierOffsetB", ClarifierOffsetB),
                new("ApfOnA", ApfOnA),
                new("ApfOnB", ApfOnB),
                new("ApfWidthA", ApfWidthA),
                new("ApfWidthB", ApfWidthB),
                new("AfGainA", AfGainA),
                new("AfGainB", AfGainB),
                new("RfGainA", RfGainA),
                new("RfGainB", RfGainB),
                new("SquelchA", SquelchA),
                new("SquelchB", SquelchB),
                new("ProcEnabled", ProcEnabled),
                new("ProcLevel", ProcLevel),
                new("AtuEnabled", AtuEnabled),
                new("AtuTuning", AtuTuning),
                new("MonitorOn", MonitorOn),
                new("MonitorLevelA", MonitorLevelA),
                new("MonitorLevelB", MonitorLevelB),
                new("VoxOn", VoxOn),
                new("VoxGain", VoxGain),
                new("VoxDelay", VoxDelay),
                new("CwPitch", CwPitch),
                new("CwSpeed", CwSpeed),
                new("CwBreakIn", CwBreakIn),
                new("CwBreakInDelay", CwBreakInDelay),
                new("FmShiftDir", FmShiftDir),
                new("FmOffsetHz", FmOffsetHz),
                new("CtcssMode", CtcssMode),
                new("CtcssTone", CtcssTone),
            };
        }

        public RadioState GetState()
        {
            return new RadioState
            {
                FrequencyA = FrequencyA,
                FrequencyB = FrequencyB,
                BandA = BandA,
                BandB = BandB,
                ModeA = ModeA ?? "",
                ModeB = ModeB ?? "",
                AntennaA = AntennaA ?? "",
                AntennaB = AntennaB ?? "",
                Power = Power,
                AfGainA = AfGainA,
                AfGainB = AfGainB,
                MicGain = MicGain,
                Controls = Controls
            };
        }

        public void UpdateBandFromFrequency()
        {
            var newBandA = GetBandFromFrequency(FrequencyA);
            var newBandB = GetBandFromFrequency(FrequencyB);

            _logger.LogInformation("[UpdateBandFromFrequency] FreqA={FreqA} -> BandA={OldBandA} -> {NewBandA}, FreqB={FreqB} -> BandB={OldBandB} -> {NewBandB}",
                FrequencyA, BandA, newBandA, FrequencyB, BandB, newBandB);

            BandA = newBandA;
            BandB = newBandB;
        }
        /// <summary>
        /// Band name for a frequency, in the operator's own IARU region.
        ///
        /// This used to be a hardcoded ladder here, region-blind and generous
        /// with the edges — it disagreed with the browser's region-aware
        /// BAND_EDGES (R1 80m: 3.800 there, 4.000 here). BandPlanService now
        /// answers from wwwroot/bandplan.default.json, the same table the
        /// browser uses. Off-band still returns "Unknown", so every existing
        /// consumer behaves as before.
        /// </summary>
        public string GetBandFromFrequency(long freq) => _bandPlan.BandForFrequency(freq);

        public event PropertyChangedEventHandler? PropertyChanged;
        protected virtual void OnPropertyChanged([CallerMemberName] string? propertyName = null)
        {
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));
        }

        public void UpdateFrequencyB(long freq)
        {
            _frequencyB = freq;
        }

        public RadioState ToRadioState()
        {
            return new RadioState
            {
                FrequencyA = FrequencyA,
                FrequencyB = FrequencyB,
                BandA = BandA,
                BandB = BandB,
                ModeA = ModeA ?? "",
                ModeB = ModeB ?? "",
                AntennaA = AntennaA ?? "",
                AntennaB = AntennaB ?? "",
                RoofingFilterA = RoofingFilterA ?? "",
                RoofingFilterB = RoofingFilterB ?? "",
                Power = Power,
                AfGainA = AfGainA,
                AfGainB = AfGainB,
                MicGain = MicGain,
                AgcA = AgcA,
                AgcB = AgcB,
                IpoA = IpoA,
                IpoB = IpoB,
                AttA = AttA,
                AttB = AttB,
                NrA = NrA,
                NrB = NrB,
                AutoNotchA = AutoNotchA,
                AutoNotchB = AutoNotchB,
                ManualNotchA = ManualNotchA,
                ManualNotchB = ManualNotchB,
                ManualNotchWidthA = ManualNotchWidthA,
                ManualNotchWidthB = ManualNotchWidthB,
                IfShapeA = IfShapeA,
                IfShapeB = IfShapeB,
                IfWidthA = IfWidthA,
                IfWidthB = IfWidthB,
                SelectedFilterA = SelectedFilterA,
                SelectedFilterB = SelectedFilterB,
                IfShiftA = IfShiftA,
                IfShiftB = IfShiftB,
                ClarifierOffsetA = ClarifierOffsetA,
                ClarifierOffsetB = ClarifierOffsetB,
                ApfOnA = ApfOnA,
                ApfOnB = ApfOnB,
                ApfWidthA = ApfWidthA,
                ApfWidthB = ApfWidthB,
                ProcEnabled = ProcEnabled,
                ProcLevel = ProcLevel
            };
        }
    }
}