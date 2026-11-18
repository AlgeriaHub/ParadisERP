import { SYSTEM_FONT, UI_FONTS, uiFontStack } from './ui-fonts';

describe('interface fonts', () => {
  // Mirrors AlgeriaPos.Application/Regional/UiFonts.All. The server rejects anything else, so a
  // key added on one side only would leave the operator picking a face the till cannot render.
  const serverKeys = ['system', 'thmanyah-sans', 'cairo', 'noto-sans-arabic', 'tajawal', 'ibm-plex-sans-arabic'];

  it('offer exactly the keys the server accepts', () => {
    expect(UI_FONTS.map((f) => f.key)).toEqual(serverKeys);
  });

  it('name a real family for every key but the system one', () => {
    for (const font of UI_FONTS) {
      if (font.key === SYSTEM_FONT) {
        expect(font.stack).toBe('');
      } else {
        expect(font.stack).toContain(font.label);
        // A Latin fallback has to sit behind the Arabic face; the till shows French too.
        expect(font.stack).toContain('Segoe UI');
      }
    }
  });

  it('fall back to the device faces rather than leaving the till unstyled', () => {
    const thmanyah = uiFontStack('thmanyah-sans');
    expect(thmanyah).toContain('Thmanyah Sans');
    expect(thmanyah.indexOf('Thmanyah Sans')).toBeLessThan(thmanyah.indexOf('Readex Pro'));
    expect(thmanyah.indexOf('Readex Pro')).toBeLessThan(thmanyah.indexOf('Cairo'));
    expect(uiFontStack('cairo')).toContain('Cairo');
    expect(uiFontStack('  CAIRO  ')).toContain('Cairo');
    expect(uiFontStack(SYSTEM_FONT)).toBe('');
    expect(uiFontStack('comic-sans')).toBe('');
    expect(uiFontStack('')).toBe('');
    expect(uiFontStack(null)).toBe('');
    expect(uiFontStack(undefined)).toBe('');
  });
});
