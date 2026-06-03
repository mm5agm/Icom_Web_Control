// Yaesu Web Control – Default Calibration Tables
// Pure data only. No functions, no DOM, no side effects.
//
// Keys match the meterName strings passed to calibrateNumeric() in calibration-engine.js.
// These tables are used as fallbacks when no backend calibration data is loaded.
// Override them at runtime via CalibrationEngine.loadFromBackend().
//
// Table format: [{ raw: <0-255 ADC reading>, value: <calibrated display value> }, ...]
// All tables must be sorted ascending by raw.

export const defaultTables = {

    // S-meter numeric scale (0–255 ADC → 0–60 calibrated S-units)
    SMETER: [
        { raw: 0,   value: 0  },
        { raw: 20,  value: 1  },
        { raw: 40,  value: 3  },
        { raw: 80,  value: 5  },
        { raw: 120, value: 7  },
        { raw: 160, value: 9  },
        { raw: 200, value: 20 },
        { raw: 255, value: 60 }
    ],

    // S-meter label snapping (0–255 ADC → nearest S-unit label string)
    SMETER_LABELS: [
        { raw: 0,   label: 'S1'  },
        { raw: 20,  label: 'S3'  },
        { raw: 40,  label: 'S5'  },
        { raw: 80,  label: 'S7'  },
        { raw: 120, label: 'S9'  },
        { raw: 160, label: '+10' },
        { raw: 200, label: '+20' },
        { raw: 240, label: '+40' }
    ],

    // Power output — RM5 (0–255 ADC → 0–200 watts)
    PWR: [
        { raw: 0,   value: 0   },
        { raw: 64,  value: 25  },
        { raw: 128, value: 50  },
        { raw: 192, value: 100 },
        { raw: 255, value: 200 }
    ],

    // SWR — MS03+RM0 right meter (0–255 ADC → SWR ratio)
    // Scale matches friend's FTdx101MP measurements: percentage = raw/255*100,
    // then lookup { 1.0:0%, 1.5:20%, 2.0:30%, 3.0:50%, 5.0:68%, 9.9:95% }.
    SWR: [
        { raw: 0,   value: 1.0 },
        { raw: 51,  value: 1.5 },
        { raw: 77,  value: 2.0 },
        { raw: 128, value: 3.0 },
        { raw: 173, value: 5.0 },
        { raw: 242, value: 9.9 }
    ],

    // Compression — RM3 (0–255 ADC → 0–20 dB)
    // Scale matches friend's FTdx101MP table: { 0dB:0%, 5dB:22%, 10dB:40%, 15dB:55%, 20dB:80% }.
    Compression: [
        { raw: 0,   value: 0  },
        { raw: 56,  value: 5  },
        { raw: 102, value: 10 },
        { raw: 140, value: 15 },
        { raw: 204, value: 20 }
    ],

    // ALC — RM4 (0–255 ADC → 0–50 volts)
    // Per FTdx101 service data: raw 178 corresponds to 50 V full-scale.
    ALC: [
        { raw: 0,   value: 0  },
        { raw: 178, value: 50 },
        { raw: 255, value: 72 }
    ],

    // Drain current IDD — RM7 (0–255 ADC → 0–25 amps)
    // Scale matches friend's FTdx101MP table: { 0A:0%, 5A:20%, 10A:40%, 15A:60%, 20A:80%, 25A:95% }.
    IDD: [
        { raw: 0,   value: 0  },
        { raw: 51,  value: 5  },
        { raw: 102, value: 10 },
        { raw: 153, value: 15 },
        { raw: 204, value: 20 },
        { raw: 242, value: 25 }
    ],

    // PA supply voltage VDD — RM8 (0–255 ADC → 0–60 volts)
    // The valid operating range is 40–55 V (raw ≈ 170–235).
    // Full table covers 0–255 to avoid identity fall-through for out-of-range readings.
    VPA: [
        { raw: 0,   value: 0  },
        { raw: 170, value: 40 },
        { raw: 204, value: 48 },
        { raw: 235, value: 55 },
        { raw: 255, value: 60 }
    ],

    // PA temperature — RM9 (0-255 ADC → 0-100 °C)
    // Friend's formula: temp = (raw / 2.3) - 6
    TPA: [
        { raw: 14,  value: 0   },
        { raw: 60,  value: 20  },
        { raw: 106, value: 40  },
        { raw: 152, value: 60  },
        { raw: 198, value: 80  },
        { raw: 244, value: 100 }
    ]
};
