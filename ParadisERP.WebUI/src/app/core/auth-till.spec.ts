import { nextLocalSeq, offlineTillCode } from './auth.service';

describe('offlineTillCode', () => {
  it('is the current till, else the last one this browser opened, else none', () => {
    expect(offlineTillCode({ deviceCode: 'C03', lastTillCode: 'C02' })).toBe('C03');
    // After an office session the current code is empty; the PIN still opens the last till.
    expect(offlineTillCode({ deviceCode: '', lastTillCode: 'C02' })).toBe('C02');
    expect(offlineTillCode({ deviceCode: '' })).toBe('');
  });
});

describe('nextLocalSeq', () => {
  it('continues after the server on the same till, never going back below this browser', () => {
    // Another computer sold on this till up to 41.
    expect(nextLocalSeq({ deviceCode: 'C02', localSeq: 7 }, { deviceCode: 'C02', lastLocalSeq: 41 })).toBe(41);
    // This browser has unsent sales beyond what the server has seen: keep them.
    expect(nextLocalSeq({ deviceCode: 'C02', localSeq: 50 }, { deviceCode: 'C02', lastLocalSeq: 41 })).toBe(50);
  });

  it('treats the last till as the same till across an office session', () => {
    expect(nextLocalSeq({ deviceCode: '', lastTillCode: 'C02', localSeq: 50 }, { deviceCode: 'C02', lastLocalSeq: 41 })).toBe(50);
  });

  it('starts after that till’s own highest number on a different till', () => {
    expect(nextLocalSeq({ deviceCode: 'C01', localSeq: 120 }, { deviceCode: 'C05', lastLocalSeq: 3 })).toBe(3);
    expect(nextLocalSeq({ deviceCode: 'C01', localSeq: 120 }, { deviceCode: 'C05' })).toBe(0);
  });

  it('leaves the counter alone for an office session', () => {
    expect(nextLocalSeq({ deviceCode: 'C02', localSeq: 9 }, { deviceCode: '', lastLocalSeq: 0 })).toBe(9);
  });
});
