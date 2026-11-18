import { AdminProduct } from '../../../core/admin.service';
import { contractState, itemPaperName, seesCancelled } from './rental-view';

/**
 * Answer 28 (owner, 2026-10-02): an item prints under one name, Arabic or French as the admin
 * chooses, or with a second beneath. Unset, the papers keep what they always printed.
 */
describe('itemPaperName', () => {
  const rtl = (l: string) => l === 'ar';
  const products = [{ id: 'p', nameFr: 'PIED DROIT 5.00 M', nameAr: 'بوطو', nameEn: 'Prop 5 m' } as AdminProduct];
  const line = { productId: 'p', name: 'PIED DROIT 5.00 M', nameAr: 'بوطو' };

  it('keeps French with the Arabic beneath when nothing is chosen', () => {
    expect(itemPaperName(line, products, null, rtl)).toEqual({
      main: 'PIED DROIT 5.00 M',
      second: 'بوطو',
      secondLang: 'ar',
      secondDir: 'rtl',
    });
  });

  it('prints one name in the chosen language, or two in the chosen order', () => {
    expect(itemPaperName(line, products, ['ar'], rtl)).toEqual({ main: 'بوطو', second: '', secondLang: '', secondDir: 'ltr' });
    expect(itemPaperName(line, products, ['ar', 'fr'], rtl)).toEqual({
      main: 'بوطو',
      second: 'PIED DROIT 5.00 M',
      secondLang: 'fr',
      secondDir: 'ltr',
    });
  });

  it('falls back to the French name and invents nothing beneath', () => {
    const bare = { productId: 'x', name: 'COLLIER', nameAr: null };
    expect(itemPaperName(bare, [], ['ar', 'en'], rtl)).toEqual({ main: 'COLLIER', second: '', secondLang: 'en', secondDir: 'ltr' });
  });
});

/** A contract called off before its exit note (owner, 2026-10-06): its own word, and the Admin's alone. */
describe('a cancelled contract', () => {
  it('reads as cancelled, whatever else is true of it', () => {
    expect(contractState({ status: 'Cancelled', isLocked: false }, 0, 0)).toBe('cancelled');
    expect(contractState({ status: 'Open', isLocked: false }, 0, 0)).toBe('draft');
  });

  it('is shown to the Admin or a holder of rentals.cancelled, and to nobody else', () => {
    expect(seesCancelled({ role: 'Admin', permissions: [] })).toBeTrue();
    expect(seesCancelled({ role: 'Manager', permissions: ['rentals.cancelled'] })).toBeTrue();
    expect(seesCancelled({ role: 'Manager', permissions: ['rentals.create', 'rentals.edit'] })).toBeFalse();
    expect(seesCancelled({ role: 'Cashier' })).toBeFalse();
    expect(seesCancelled(null)).toBeFalse();
  });
});
