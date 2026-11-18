/**
 * The interface faces an operator may pick for a company. This is the till's half of the
 * contract in ParadiERP.Application/Regional/UiFonts.cs: the server decides which keys are
 * legal, this decides what each key renders as. Adding a face means adding an entry here, its
 * woff2 files under public/fonts and its key to the server list — nothing else changes.
 */
export interface UiFont {
  readonly key: string;
  /** The typeface's own name, never translated. Empty for the system face, which the
   *  consumer labels in the operator's language. */
  readonly label: string;
  /** Empty for the system face, which keeps whatever the stylesheet already sets. */
  readonly stack: string;
}

/** Whatever the device already has. No font file is fetched. */
export const SYSTEM_FONT = 'system';

/** Latin fallbacks stay behind each Arabic face so French labels keep an even colour. */
const FALLBACK = '"Segoe UI", system-ui, sans-serif';

export const UI_FONTS: readonly UiFont[] = [
  { key: SYSTEM_FONT, label: '', stack: '' },
  { key: 'readex-pro', label: 'Readex Pro', stack: `'Readex Pro', 'Cairo', ${FALLBACK}` },
  { key: 'cairo', label: 'Cairo', stack: `'Cairo', ${FALLBACK}` },
  { key: 'noto-sans-arabic', label: 'Noto Sans Arabic', stack: `'Noto Sans Arabic', ${FALLBACK}` },
  { key: 'tajawal', label: 'Tajawal', stack: `'Tajawal', ${FALLBACK}` },
  { key: 'ibm-plex-sans-arabic', label: 'IBM Plex Sans Arabic', stack: `'IBM Plex Sans Arabic', ${FALLBACK}` },
];

/** An unknown key falls back to the system face rather than leaving the till unstyled. */
export function uiFontStack(key: string | null | undefined): string {
  return UI_FONTS.find((f) => f.key === (key ?? '').trim().toLowerCase())?.stack ?? '';
}
