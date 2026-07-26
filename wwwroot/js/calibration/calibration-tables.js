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

    // Power output — IC-7300 Po meter (CI-V 15 11, 0–255 raw → 0–100 W).
    // The Po meter is a percentage scale (raw 0=0%, 143=50%, 213=100%); for a
    // 100 W radio 100% maps directly to 100 W.
    PWR: [
        { raw: 0,   value: 0   },
        { raw: 143, value: 50  },
        { raw: 213, value: 100 }
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

    // Compression — IC-7300 COMP meter (CI-V 15 14, 0–255 raw → 0–30 dB).
    // Manual points: raw 0=0 dB, 130=15 dB, 210=30 dB.
    Compression: [
        { raw: 0,   value: 0  },
        { raw: 130, value: 15 },
        { raw: 210, value: 30 }
    ],

    // ALC — IC-7300 ALC meter (CI-V 15 13, 0–255 raw → 0–100 %).
    // The meter is a relative scale, not volts: raw 0=minimum, 120=maximum.
    ALC: [
        { raw: 0,   value: 0   },
        { raw: 120, value: 100 }
    ],

    // Drain current IDD — IC-7300 Id meter (CI-V 15 16, 0–255 raw → 0–25 A).
    // Manual points: raw 0=0 A, 97=10 A, 146=15 A, 241=25 A.
    IDD: [
        { raw: 0,   value: 0  },
        { raw: 97,  value: 10 },
        { raw: 146, value: 15 },
        { raw: 241, value: 25 }
    ],

    // PA supply voltage VDD — IC-7300 Vd meter (CI-V 15 15, 0–255 raw → 0–16 V).
    // Manual points: raw 0=0 V, 13=10 V, 241=16 V. Nominal shack-PSU rail ≈ 13.8 V.
    VPA: [
        { raw: 0,   value: 0  },
        { raw: 13,  value: 10 },
        { raw: 241, value: 16 }
    ]
};
