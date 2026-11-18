import { DOCUMENT } from '@angular/common';
import { Injectable, computed, effect, inject, signal } from '@angular/core';

/** Light, dark, or whatever the device is set to. */
export type Mode = 'light' | 'dark' | 'system';

export const MODES: readonly Mode[] = ['light', 'dark', 'system'];

export type Theme = 'atelier' | 'atlas' | 'oasis' | 'sahara' | 'graphite';

export const THEMES: readonly Theme[] = ['atelier', 'atlas', 'oasis', 'sahara', 'graphite'];

export const THEME_SWATCHES: Record<Theme, { accent: string; ink: string }> = {
  // The default is the Paradis :root (styles.scss): indigo. The key stays `atelier`, what every device saved.
  atelier: { accent: '#4338ca', ink: '#1e1b4b' },
  atlas: { accent: '#1d4ed8', ink: '#172554' },
  oasis: { accent: '#115e59', ink: '#042f2e' },
  sahara: { accent: '#933716', ink: '#2e1a10' },
  graphite: { accent: '#6d28d9', ink: '#18181b' },
};

const KEY = 'pos.appearance.v3';
const KEY_V2 = 'pos.appearance.v2';

interface Saved {
  mode: Mode;
  theme: Theme;
}

const DEFAULT_MODE: Mode = 'light';
const DEFAULT_THEME: Theme = 'atelier';

/**
 * Atelier appearance (v3, 2026-10-12): five themes, three modes (light, dark, system).
 *
 * <p>Sets `data-mode` on `<html>` to 'light' or 'dark' (never 'system').
 * Sets `data-theme` on `<html>` to 'atelier', 'atlas', 'oasis', 'sahara', or 'graphite'.
 * Removes any obsolete `data-look` and `data-palette` attributes.</p>
 *
 * <p>Kept per device in local storage (`pos.appearance.v3`): it is how this screen looks to
 * whoever uses it, not a company setting, and a till keeps working with storage blocked.</p>
 */
@Injectable({ providedIn: 'root' })
export class ThemeService {
  private readonly document = inject(DOCUMENT);
  private readonly media = this.document.defaultView?.matchMedia?.('(prefers-color-scheme: dark)') ?? null;
  private readonly systemDark = signal(this.media?.matches ?? false);

  readonly mode = signal<Mode>(DEFAULT_MODE);
  readonly theme = signal<Theme>(DEFAULT_THEME);

  /** Light or dark as drawn. */
  readonly dark = computed(() => this.mode() === 'dark' || (this.mode() === 'system' && this.systemDark()));

  constructor() {
    const initial = read(this.document);
    this.mode.set(initial.mode);
    this.theme.set(initial.theme);
    this.media?.addEventListener?.('change', (e) => this.systemDark.set(e.matches));

    effect(() => {
      const root = this.document.documentElement;
      root.removeAttribute('data-look');
      root.removeAttribute('data-palette');
      const resolved = this.dark() ? 'dark' : 'light';
      root.setAttribute('data-mode', resolved);
      root.setAttribute('data-theme', this.theme());
      write(this.document, { mode: this.mode(), theme: this.theme() });
      const meta = this.document.querySelector('meta[name="theme-color"]');
      const ink = getComputedStyle(root).getPropertyValue('--ink').trim();
      meta?.setAttribute('content', ink || '#1e1b4b');
    });
  }

  setMode(mode: Mode): void {
    this.mode.set(mode);
  }

  setTheme(theme: Theme): void {
    this.theme.set(theme);
  }
}

function read(document: Document): { mode: Mode; theme: Theme } {
  let mode: Mode = DEFAULT_MODE;
  let theme: Theme = DEFAULT_THEME;
  try {
    let v3: Partial<Saved> | null = null;
    try {
      const raw = document.defaultView?.localStorage.getItem(KEY);
      if (raw) {
        v3 = JSON.parse(raw);
      }
    } catch {
      /* ignore JSON error in v3 */
    }

    let v2: { mode?: Mode; palette?: string } | null = null;
    try {
      const rawV2 = document.defaultView?.localStorage.getItem(KEY_V2);
      if (rawV2) {
        v2 = JSON.parse(rawV2);
      }
    } catch {
      /* ignore JSON error in v2 */
    }

    if (v3?.mode && MODES.includes(v3.mode)) {
      mode = v3.mode;
    } else if (v2?.mode && MODES.includes(v2.mode)) {
      mode = v2.mode;
    }

    if (v3?.theme && THEMES.includes(v3.theme)) {
      theme = v3.theme;
    } else if (v2?.palette === 'oasis' || v2?.palette === 'sahara' || v2?.palette === 'graphite') {
      theme = v2.palette;
    } else {
      theme = DEFAULT_THEME;
    }
  } catch {
    return { mode: DEFAULT_MODE, theme: DEFAULT_THEME };
  }
  return { mode, theme };
}

function write(document: Document, value: Saved): void {
  try {
    document.defaultView?.localStorage.setItem(KEY, JSON.stringify(value));
  } catch {
    /* storage blocked: choice lasts as long as the tab */
  }
}
