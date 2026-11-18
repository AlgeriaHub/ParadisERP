import { View, urlFor, viewFor } from './rentals-tab.component';
import { collect } from '../../../ui/invalid-summary.component';

/**
 * Every rental screen is an address (REDESIGN_PLAN.md §4): a reload, a deep link, Back and Forward
 * must all land on the screen the address names, and going to a screen must write that address.
 */
describe('rental addresses', () => {
  const views: View[] = [
    { kind: 'list' },
    { kind: 'new' },
    { kind: 'contract', id: 'c1' },
    { kind: 'edit', id: 'c1' },
    { kind: 'issue', id: 'c1' },
    { kind: 'return', id: 'c1' },
    { kind: 'extend', id: 'c1' },
  ];

  it('writes each view at its own address and reads it back unchanged', () => {
    for (const view of views) {
      const url = urlFor(view);
      expect(url.slice(0, 3)).toEqual(['/backoffice', 'rentals', 'contracts']);
      expect(viewFor(url.slice(3))).withContext(url.join('/')).toEqual(view);
    }
  });

  it('gives the documents the addresses of the route map', () => {
    expect(urlFor({ kind: 'contract', id: 'c1' }).join('/')).toBe('/backoffice/rentals/contracts/c1');
    expect(urlFor({ kind: 'issue', id: 'c1' }).join('/')).toBe('/backoffice/rentals/contracts/c1/issue');
    expect(urlFor({ kind: 'new' }).join('/')).toBe('/backoffice/rentals/contracts/new');
  });

  it('reads an address it does not recognise as the list, never as a half-made screen', () => {
    expect(viewFor(['c1', 'nonsense'])).toEqual({ kind: 'list' });
    expect(viewFor(['c1', 'issue', 'extra'])).toEqual({ kind: 'list' });
    expect(viewFor(['new', 'x'])).toEqual({ kind: 'list' });
  });
});

describe('form error summary', () => {
  it('lists the fields the form marked invalid, by label without the Required word, with their own error', () => {
    const form = document.createElement('div');
    form.innerHTML = `
      <div class="bo-field is-invalid">
        <label for="f-a">Customer<span class="bo-req">Required</span></label>
        <select id="f-a"></select>
        <span class="bo-error">This field is required.</span>
      </div>
      <div class="bo-field">
        <label for="f-b">Notes</label>
        <input id="f-b" />
      </div>
      <div class="bo-field is-invalid">
        <label for="f-c">Quantity</label>
        <input id="f-c" />
      </div>`;
    expect(collect(form)).toEqual([
      { id: 'f-a', label: 'Customer', message: 'This field is required.' },
      { id: 'f-c', label: 'Quantity', message: '' },
    ]);
  });
});
