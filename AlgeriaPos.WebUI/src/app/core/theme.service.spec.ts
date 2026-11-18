import { TestBed } from '@angular/core/testing';
import { ThemeService } from './theme.service';

/**
 * Atelier appearance (v3, 2026-10-12): one look, three modes (light, dark, system).
 */
describe('ThemeService', () => {
  const KEY_V3 = 'pos.appearance.v3';
  const KEY_V2 = 'pos.appearance.v2';
  const root = document.documentElement;

  function start(savedV3?: string): ThemeService {
    if (savedV3 === undefined) localStorage.removeItem(KEY_V3);
    else localStorage.setItem(KEY_V3, savedV3);
    TestBed.resetTestingModule();
    const theme = TestBed.inject(ThemeService);
    TestBed.flushEffects();
    return theme;
  }

  afterEach(() => {
    localStorage.removeItem(KEY_V3);
    localStorage.removeItem(KEY_V2);
    root.removeAttribute('data-look');
    root.removeAttribute('data-palette');
    root.removeAttribute('data-mode');
    root.removeAttribute('data-theme');
  });

  it('starts in light mode by default', () => {
    const theme = start();
    expect(theme.mode()).toBe('light');
    expect(theme.dark()).toBeFalse();
    expect(root.getAttribute('data-mode')).toBe('light');
  });

  it('restores a saved v3 mode', () => {
    const theme = start(JSON.stringify({ mode: 'dark' }));
    expect(theme.mode()).toBe('dark');
    expect(theme.dark()).toBeTrue();
    expect(root.getAttribute('data-mode')).toBe('dark');
  });

  it('falls back to light on an invalid or unreadable value', () => {
    expect(start('{not json').mode()).toBe('light');
    const odd = start(JSON.stringify({ mode: 'sepia' }));
    expect(odd.mode()).toBe('light');
    expect(odd.dark()).toBeFalse();
    expect(root.getAttribute('data-mode')).toBe('light');
  });

  it('migrates an existing v2 value when v3 is absent', () => {
    localStorage.setItem(KEY_V2, JSON.stringify({ mode: 'dark' }));
    const theme = start();
    expect(theme.mode()).toBe('dark');
    expect(theme.dark()).toBeTrue();
    expect(root.getAttribute('data-mode')).toBe('dark');
  });

  it('follows the media query when mode is system', () => {
    const theme = start(JSON.stringify({ mode: 'system' }));
    expect(theme.mode()).toBe('system');
    const mediaDark = window.matchMedia?.('(prefers-color-scheme: dark)').matches ?? false;
    expect(theme.dark()).toBe(mediaDark);
    expect(root.getAttribute('data-mode')).toBe(mediaDark ? 'dark' : 'light');
  });

  it('persists a change to v3 storage', () => {
    const theme = start();
    theme.setMode('dark');
    TestBed.flushEffects();
    expect(JSON.parse(localStorage.getItem(KEY_V3)!)).toEqual({ mode: 'dark', theme: 'atelier' });
    expect(root.getAttribute('data-mode')).toBe('dark');
  });

  it('defaults to atelier theme and sets data-theme="atelier"', () => {
    const theme = start();
    expect(theme.theme()).toBe('atelier');
    expect(root.getAttribute('data-theme')).toBe('atelier');
  });

  it('restores a saved v3 theme', () => {
    const theme = start(JSON.stringify({ mode: 'light', theme: 'oasis' }));
    expect(theme.theme()).toBe('oasis');
    expect(root.getAttribute('data-theme')).toBe('oasis');
  });

  it('falls back to atelier on an invalid theme', () => {
    expect(start('{not json').theme()).toBe('atelier');
    const odd = start(JSON.stringify({ theme: 'sepia' }));
    expect(odd.theme()).toBe('atelier');
    expect(root.getAttribute('data-theme')).toBe('atelier');
  });

  it('migrates v2 palette "oasis" to oasis', () => {
    localStorage.setItem(KEY_V2, JSON.stringify({ palette: 'oasis' }));
    const theme = start();
    expect(theme.theme()).toBe('oasis');
    expect(root.getAttribute('data-theme')).toBe('oasis');
  });

  it('gives atelier when v2 palette is "atlas"', () => {
    localStorage.setItem(KEY_V2, JSON.stringify({ palette: 'atlas' }));
    const theme = start();
    expect(theme.theme()).toBe('atelier');
    expect(root.getAttribute('data-theme')).toBe('atelier');
  });

  it('persists setTheme and sets data-theme', () => {
    const theme = start();
    theme.setTheme('sahara');
    TestBed.flushEffects();
    expect(JSON.parse(localStorage.getItem(KEY_V3)!)).toEqual({ mode: 'light', theme: 'sahara' });
    expect(root.getAttribute('data-theme')).toBe('sahara');
  });

  it('removes data-look and data-palette attributes', () => {
    root.setAttribute('data-look', 'new');
    root.setAttribute('data-palette', 'previous');
    start();
    expect(root.hasAttribute('data-look')).toBeFalse();
    expect(root.hasAttribute('data-palette')).toBeFalse();
  });
});
