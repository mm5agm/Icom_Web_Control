// Band segment data for IARU Region 1, Region 2, Region 3, and Japan.
// Frequencies in Hz. Each entry gives the representative dial frequency and
// the mode string used by this app (matches CatMessageDispatcher mode names).
//
// Region 1 = Europe, Africa, Middle East, Northern Asia (IARU R1 band plan)
// Region 2 = Americas (IARU R2; USA FCC Part 97 used as primary reference)
// Region 3 = Asia-Pacific excluding Japan (IARU R3 band plan)
// Japan    = JARL band plan (differs from IARU R3 in several key areas)
//
// FT8 frequencies (14.074, 7.074 etc.) are the same worldwide regardless of region.
// Differences are mainly in the SSB segment start, 80m/40m phone calling areas,
// and 60m allocations.

export const BAND_PLANS = {
    Region1: {
        '160m': {
            CW:   { freq:  1820000, mode: 'CW-U',   label: 'CW' },
            FT8:  { freq:  1840000, mode: 'DATA-U',  label: 'FT8' },
            SSB:  { freq:  1850000, mode: 'LSB',     label: 'SSB' }
        },
        '80m': {
            CW:   { freq:  3520000, mode: 'CW-U',   label: 'CW' },
            FT8:  { freq:  3573000, mode: 'DATA-U',  label: 'FT8' },
            RTTY: { freq:  3580000, mode: 'RTTY-L',  label: 'RTTY' },
            SSB:  { freq:  3690000, mode: 'LSB',     label: 'SSB' }
        },
        '60m': {
            // IARU R1 secondary allocation 5351.5–5366.5 kHz (WRC-15).
            // Individual countries within R1 have their own channel plans;
            // these entries cover the standard FT8 spot and mid-band USB.
            FT8:  { freq:  5357000, mode: 'USB',     label: 'FT8' },
            USB:  { freq:  5362000, mode: 'USB',     label: 'USB' }
        },
        '40m': {
            CW:   { freq:  7020000, mode: 'CW-U',   label: 'CW' },
            FT8:  { freq:  7074000, mode: 'DATA-U',  label: 'FT8' },
            RTTY: { freq:  7040000, mode: 'RTTY-L',  label: 'RTTY' },
            SSB:  { freq:  7090000, mode: 'LSB',     label: 'SSB' }
        },
        '30m': {
            CW:   { freq: 10115000, mode: 'CW-U',   label: 'CW' },
            FT8:  { freq: 10136000, mode: 'DATA-U',  label: 'FT8' }
        },
        '20m': {
            CW:   { freq: 14025000, mode: 'CW-U',   label: 'CW' },
            FT8:  { freq: 14074000, mode: 'DATA-U',  label: 'FT8' },
            RTTY: { freq: 14080000, mode: 'RTTY-U',  label: 'RTTY' },
            SSB:  { freq: 14225000, mode: 'USB',     label: 'SSB' }
        },
        '17m': {
            CW:   { freq: 18080000, mode: 'CW-U',   label: 'CW' },
            FT8:  { freq: 18100000, mode: 'DATA-U',  label: 'FT8' },
            SSB:  { freq: 18130000, mode: 'USB',     label: 'SSB' }
        },
        '15m': {
            CW:   { freq: 21025000, mode: 'CW-U',   label: 'CW' },
            FT8:  { freq: 21074000, mode: 'DATA-U',  label: 'FT8' },
            RTTY: { freq: 21080000, mode: 'RTTY-U',  label: 'RTTY' },
            SSB:  { freq: 21280000, mode: 'USB',     label: 'SSB' }
        },
        '12m': {
            CW:   { freq: 24895000, mode: 'CW-U',   label: 'CW' },
            FT8:  { freq: 24915000, mode: 'DATA-U',  label: 'FT8' },
            SSB:  { freq: 24940000, mode: 'USB',     label: 'SSB' }
        },
        '10m': {
            CW:   { freq: 28025000, mode: 'CW-U',   label: 'CW' },
            FT8:  { freq: 28074000, mode: 'DATA-U',  label: 'FT8' },
            RTTY: { freq: 28080000, mode: 'RTTY-U',  label: 'RTTY' },
            SSB:  { freq: 28500000, mode: 'USB',     label: 'SSB' }
        },
        '6m': {
            CW:   { freq: 50050000, mode: 'CW-U',   label: 'CW' },
            FT8:  { freq: 50313000, mode: 'DATA-U',  label: 'FT8' },
            SSB:  { freq: 50150000, mode: 'USB',     label: 'SSB' }
        },
        '4m': {
            // 70 MHz band; available in many Region 1 countries
            CW:   { freq: 70050000, mode: 'CW-U',   label: 'CW' },
            FT8:  { freq: 70154000, mode: 'DATA-U',  label: 'FT8' },
            SSB:  { freq: 70200000, mode: 'USB',     label: 'SSB' }
        }
    },

    Region2: {
        '160m': {
            CW:   { freq:  1820000, mode: 'CW-U',   label: 'CW' },
            FT8:  { freq:  1840000, mode: 'DATA-U',  label: 'FT8' },
            SSB:  { freq:  1850000, mode: 'LSB',     label: 'SSB' }
        },
        '80m': {
            CW:   { freq:  3510000, mode: 'CW-U',   label: 'CW' },
            FT8:  { freq:  3573000, mode: 'DATA-U',  label: 'FT8' },
            SSB:  { freq:  3800000, mode: 'LSB',     label: 'SSB' }
        },
        '60m': {
            // USA FCC Part 97 channels (primary R2 reference; dial frequencies shown)
            CH1:  { freq:  5330500, mode: 'USB',     label: '5.331' },
            CH2:  { freq:  5346500, mode: 'USB',     label: '5.347' },
            CH3:  { freq:  5357000, mode: 'USB',     label: '5.357' },
            CH4:  { freq:  5371500, mode: 'USB',     label: '5.372' },
            CH5:  { freq:  5403500, mode: 'USB',     label: '5.404' }
        },
        '40m': {
            CW:   { freq:  7010000, mode: 'CW-U',   label: 'CW' },
            FT8:  { freq:  7074000, mode: 'DATA-U',  label: 'FT8' },
            RTTY: { freq:  7080000, mode: 'RTTY-L',  label: 'RTTY' },
            SSB:  { freq:  7200000, mode: 'LSB',     label: 'SSB' }
        },
        '30m': {
            CW:   { freq: 10115000, mode: 'CW-U',   label: 'CW' },
            FT8:  { freq: 10136000, mode: 'DATA-U',  label: 'FT8' }
        },
        '20m': {
            CW:   { freq: 14025000, mode: 'CW-U',   label: 'CW' },
            FT8:  { freq: 14074000, mode: 'DATA-U',  label: 'FT8' },
            RTTY: { freq: 14080000, mode: 'RTTY-U',  label: 'RTTY' },
            SSB:  { freq: 14225000, mode: 'USB',     label: 'SSB' }
        },
        '17m': {
            CW:   { freq: 18080000, mode: 'CW-U',   label: 'CW' },
            FT8:  { freq: 18100000, mode: 'DATA-U',  label: 'FT8' },
            SSB:  { freq: 18130000, mode: 'USB',     label: 'SSB' }
        },
        '15m': {
            CW:   { freq: 21025000, mode: 'CW-U',   label: 'CW' },
            FT8:  { freq: 21074000, mode: 'DATA-U',  label: 'FT8' },
            RTTY: { freq: 21080000, mode: 'RTTY-U',  label: 'RTTY' },
            SSB:  { freq: 21300000, mode: 'USB',     label: 'SSB' }
        },
        '12m': {
            CW:   { freq: 24895000, mode: 'CW-U',   label: 'CW' },
            FT8:  { freq: 24915000, mode: 'DATA-U',  label: 'FT8' },
            SSB:  { freq: 24940000, mode: 'USB',     label: 'SSB' }
        },
        '10m': {
            CW:   { freq: 28025000, mode: 'CW-U',   label: 'CW' },
            FT8:  { freq: 28074000, mode: 'DATA-U',  label: 'FT8' },
            RTTY: { freq: 28080000, mode: 'RTTY-U',  label: 'RTTY' },
            SSB:  { freq: 28500000, mode: 'USB',     label: 'SSB' }
        },
        '6m': {
            CW:   { freq: 50050000, mode: 'CW-U',   label: 'CW' },
            FT8:  { freq: 50313000, mode: 'DATA-U',  label: 'FT8' },
            SSB:  { freq: 50125000, mode: 'USB',     label: 'SSB' }
        }
        // No 4m (70 MHz) allocation in Region 2
    },

    Region3: {
        '160m': {
            CW:   { freq:  1820000, mode: 'CW-U',   label: 'CW' },
            FT8:  { freq:  1840000, mode: 'DATA-U',  label: 'FT8' },
            SSB:  { freq:  1850000, mode: 'LSB',     label: 'SSB' }
        },
        '80m': {
            // R3 phone segment starts higher than R1 (~3700–3900 kHz)
            CW:   { freq:  3520000, mode: 'CW-U',   label: 'CW' },
            FT8:  { freq:  3573000, mode: 'DATA-U',  label: 'FT8' },
            RTTY: { freq:  3580000, mode: 'RTTY-L',  label: 'RTTY' },
            SSB:  { freq:  3770000, mode: 'LSB',     label: 'SSB' }
        },
        '60m': {
            // WRC-15 secondary 5351.5–5366.5 kHz; access varies by country in R3
            FT8:  { freq:  5357000, mode: 'USB',     label: 'FT8' },
            USB:  { freq:  5362000, mode: 'USB',     label: 'USB' }
        },
        '40m': {
            // R3 phone segment 7100–7300 kHz
            CW:   { freq:  7020000, mode: 'CW-U',   label: 'CW' },
            FT8:  { freq:  7074000, mode: 'DATA-U',  label: 'FT8' },
            RTTY: { freq:  7040000, mode: 'RTTY-L',  label: 'RTTY' },
            SSB:  { freq:  7100000, mode: 'LSB',     label: 'SSB' }
        },
        '30m': {
            CW:   { freq: 10115000, mode: 'CW-U',   label: 'CW' },
            FT8:  { freq: 10136000, mode: 'DATA-U',  label: 'FT8' }
        },
        '20m': {
            CW:   { freq: 14025000, mode: 'CW-U',   label: 'CW' },
            FT8:  { freq: 14074000, mode: 'DATA-U',  label: 'FT8' },
            RTTY: { freq: 14080000, mode: 'RTTY-U',  label: 'RTTY' },
            SSB:  { freq: 14225000, mode: 'USB',     label: 'SSB' }
        },
        '17m': {
            CW:   { freq: 18080000, mode: 'CW-U',   label: 'CW' },
            FT8:  { freq: 18100000, mode: 'DATA-U',  label: 'FT8' },
            SSB:  { freq: 18130000, mode: 'USB',     label: 'SSB' }
        },
        '15m': {
            CW:   { freq: 21025000, mode: 'CW-U',   label: 'CW' },
            FT8:  { freq: 21074000, mode: 'DATA-U',  label: 'FT8' },
            RTTY: { freq: 21080000, mode: 'RTTY-U',  label: 'RTTY' },
            SSB:  { freq: 21290000, mode: 'USB',     label: 'SSB' }
        },
        '12m': {
            CW:   { freq: 24895000, mode: 'CW-U',   label: 'CW' },
            FT8:  { freq: 24915000, mode: 'DATA-U',  label: 'FT8' },
            SSB:  { freq: 24940000, mode: 'USB',     label: 'SSB' }
        },
        '10m': {
            CW:   { freq: 28025000, mode: 'CW-U',   label: 'CW' },
            FT8:  { freq: 28074000, mode: 'DATA-U',  label: 'FT8' },
            RTTY: { freq: 28080000, mode: 'RTTY-U',  label: 'RTTY' },
            SSB:  { freq: 28500000, mode: 'USB',     label: 'SSB' }
        },
        '6m': {
            CW:   { freq: 50050000, mode: 'CW-U',   label: 'CW' },
            FT8:  { freq: 50313000, mode: 'DATA-U',  label: 'FT8' },
            SSB:  { freq: 50150000, mode: 'USB',     label: 'SSB' }
        }
        // No 4m (70 MHz) allocation in Region 3
    },

    Japan: {
        '160m': {
            // JA primary allocation 1810–1825 kHz (CW/narrow); phone on 1907.5–1912.5 kHz
            CW:   { freq:  1820000, mode: 'CW-U',   label: 'CW' },
            FT8:  { freq:  1840000, mode: 'DATA-U',  label: 'FT8' },
            SSB:  { freq:  1908000, mode: 'LSB',     label: 'SSB (1.9M)' }
        },
        '80m': {
            // JA phone segment ~3700–3800 kHz
            CW:   { freq:  3520000, mode: 'CW-U',   label: 'CW' },
            FT8:  { freq:  3573000, mode: 'DATA-U',  label: 'FT8' },
            SSB:  { freq:  3740000, mode: 'LSB',     label: 'SSB' }
        },
        // No 60m secondary allocation in Japan
        '40m': {
            // JA phone segment 7100–7200 kHz
            CW:   { freq:  7025000, mode: 'CW-U',   label: 'CW' },
            FT8:  { freq:  7074000, mode: 'DATA-U',  label: 'FT8' },
            SSB:  { freq:  7100000, mode: 'LSB',     label: 'SSB' }
        },
        '30m': {
            CW:   { freq: 10115000, mode: 'CW-U',   label: 'CW' },
            FT8:  { freq: 10136000, mode: 'DATA-U',  label: 'FT8' }
        },
        '20m': {
            CW:   { freq: 14025000, mode: 'CW-U',   label: 'CW' },
            FT8:  { freq: 14074000, mode: 'DATA-U',  label: 'FT8' },
            RTTY: { freq: 14080000, mode: 'RTTY-U',  label: 'RTTY' },
            SSB:  { freq: 14225000, mode: 'USB',     label: 'SSB' }
        },
        '17m': {
            CW:   { freq: 18080000, mode: 'CW-U',   label: 'CW' },
            FT8:  { freq: 18100000, mode: 'DATA-U',  label: 'FT8' },
            SSB:  { freq: 18130000, mode: 'USB',     label: 'SSB' }
        },
        '15m': {
            CW:   { freq: 21025000, mode: 'CW-U',   label: 'CW' },
            FT8:  { freq: 21074000, mode: 'DATA-U',  label: 'FT8' },
            RTTY: { freq: 21080000, mode: 'RTTY-U',  label: 'RTTY' },
            SSB:  { freq: 21290000, mode: 'USB',     label: 'SSB' }
        },
        '12m': {
            CW:   { freq: 24895000, mode: 'CW-U',   label: 'CW' },
            FT8:  { freq: 24915000, mode: 'DATA-U',  label: 'FT8' },
            SSB:  { freq: 24940000, mode: 'USB',     label: 'SSB' }
        },
        '10m': {
            CW:   { freq: 28025000, mode: 'CW-U',   label: 'CW' },
            FT8:  { freq: 28074000, mode: 'DATA-U',  label: 'FT8' },
            RTTY: { freq: 28080000, mode: 'RTTY-U',  label: 'RTTY' },
            SSB:  { freq: 28500000, mode: 'USB',     label: 'SSB' }
        },
        '6m': {
            CW:   { freq: 50050000, mode: 'CW-U',   label: 'CW' },
            FT8:  { freq: 50313000, mode: 'DATA-U',  label: 'FT8' },
            SSB:  { freq: 50150000, mode: 'USB',     label: 'SSB' }
        }
        // No 4m (70 MHz) allocation in Japan
    }
};

// Backward-compatibility aliases: settings saved when the options were "UK" and "USA"
// continue to resolve to the correct plan without requiring a manual settings update.
BAND_PLANS.UK  = BAND_PLANS.Region1;
BAND_PLANS.USA = BAND_PLANS.Region2;

export function getSegments(bandPlan, band) {
    const plan = BAND_PLANS[bandPlan] || BAND_PLANS['Region1'];
    return plan[band] || null;
}
