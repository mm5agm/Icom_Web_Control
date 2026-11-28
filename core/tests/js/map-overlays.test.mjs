import { test } from 'node:test';
import assert from 'node:assert/strict';
import { decodeRegions, locatorAt, gridLevelFor, gridFor } from '../../js/spotmap/map-overlays.js';
import { gridToLatLon } from '../../js/spotmap/spot-data.js';
import { REGIONS } from '../../js/spotmap/region-data.js';

test('locatorAt gives known locators', () => {
    assert.equal(locatorAt(55.95, -3.19, 6), 'IO85jw');   // Edinburgh
    assert.equal(locatorAt(41.71, -72.73, 4), 'FN31');    // Connecticut
    assert.equal(locatorAt(-33.87, 151.21, 2), 'QF');     // Sydney
    assert.equal(locatorAt(41.71, -72.73 + 360, 4), 'FN31', 'a panned-past-180 longitude wraps');
});

test('locatorAt agrees with gridToLatLon', () => {
    for (const g of ['FN31', 'IO85', 'JO01', 'PM95', 'RE78', 'AA00', 'RR99']) {
        const [lat, lon] = gridToLatLon(g);
        assert.equal(locatorAt(lat, lon, 4), g);
    }
});

test('the grid gets finer as the map zooms in', () => {
    assert.equal(gridLevelFor(2).chars, 2);
    assert.equal(gridLevelFor(5).chars, 4);
    assert.equal(gridLevelFor(11).chars, 6);
});

test('gridFor gives lines and a label for each square in view', () => {
    const g = gridFor({ south: 40.2, west: -73.5, north: 41.8, east: -70.5 }, 6);
    assert.equal(g.level.chars, 4);
    assert.equal(g.labels.length, 2 * 2);   // lon -74..-70 = 2 squares, lat 40..42 = 2
    assert.ok(g.labels.some(l => l.text === 'FN31'));
    assert.equal(g.lines.length, 3 + 3);
});

test('gridFor keeps to the poles and gives up on huge views', () => {
    const g = gridFor({ south: -120, west: -200, north: 120, east: 200 }, 1);
    for (const l of g.lines) for (const [lat] of l) assert.ok(lat >= -90 && lat <= 90);
    assert.deepEqual(gridFor({ south: -80, west: -180, north: 80, east: 180 }, 11, 100).lines, []);
});

test('region data decodes, with all 50 US states and DC', () => {
    const r = decodeRegions(REGIONS);
    const us = r.filter(x => x.cc === 'US');
    assert.equal(us.length, 51);
    const tx = us.find(x => x.abbr === 'TX');
    assert.equal(tx.name, 'Texas');
    for (const ring of tx.rings) for (const [lat, lon] of ring) {
        assert.ok(lat > 25 && lat < 37 && lon > -107 && lon < -93, `${lat},${lon}`);
    }
    assert.ok(r.some(x => x.cc === 'CA' && x.name === 'Ontario'));
    assert.ok(r.some(x => x.cc === 'AU' && x.abbr === 'NSW'));
});
