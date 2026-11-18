import { Component, signal } from '@angular/core';
import { TestBed } from '@angular/core/testing';
import { TRANSLATIONS } from '../core/i18n/translations';
import { ListFooterComponent, pageItems } from './list-footer.component';
import { DEFAULT_PAGE_SIZE, PAGE_SIZES, PrintAllRows, createPager } from './pager';

const rows = (n: number) => Array.from({ length: n }, (_, i) => i + 1);

/** Every list pages: ten rows to begin with, 20, 50 or 100 at the user's choice (owner, 2026-11-17). */
describe('createPager', () => {
  const make = (source: () => readonly number[], options = {}) =>
    TestBed.runInInjectionContext(() => createPager(source, options));

  it('starts at ten rows on the first page', () => {
    expect(DEFAULT_PAGE_SIZE).toBe(10);
    expect(PAGE_SIZES).toEqual([10, 20, 50, 100]);
    const pager = make(() => rows(35));
    expect(pager.size()).toBe(10);
    expect(pager.rows()).toEqual(rows(10));
    expect(pager.total()).toBe(35);
    expect(pager.pageCount()).toBe(4);
  });

  it('turns pages and never goes past the last one', () => {
    const data = signal(rows(35));
    const pager = make(() => data());
    pager.go(3);
    expect(pager.rows()).toEqual([31, 32, 33, 34, 35]);
    data.set(rows(12)); // a refresh that shrank the list
    expect(pager.index()).toBe(1);
    expect(pager.rows()).toEqual([11, 12]);
  });

  it('keeps the first row on screen in view when the size changes, and refuses sizes it does not offer', () => {
    const pager = make(() => rows(250));
    pager.go(7); // rows 71–80
    pager.setSize(50);
    expect(pager.index()).toBe(1); // rows 51–100 hold row 71
    expect(pager.rows()[0]).toBe(51);
    pager.setSize(33);
    expect(pager.size()).toBe(10);
  });

  it('goes back to page one when the filters change, not when the rows are refreshed', () => {
    const query = signal('');
    const data = signal(rows(40));
    const pager = make(() => data(), { resetOn: () => [query()] });
    pager.go(2);
    data.set(rows(41)); // a save that reloaded the list
    expect(pager.index()).toBe(2);
    query.set('a');
    expect(pager.index()).toBe(0);
    pager.go(1);
    expect(pager.index()).toBe(1);
  });

  it('remembers the size picked on a list for the session', () => {
    make(() => rows(5), { key: 'spec.remembered' }).setSize(50);
    expect(make(() => rows(5), { key: 'spec.remembered' }).size()).toBe(50);
    expect(make(() => rows(5), { key: 'spec.other' }).size()).toBe(10);
  });

  it('shows every row while the browser prints', () => {
    const pager = make(() => rows(35));
    const printing = TestBed.inject(PrintAllRows);
    printing.active.set(true);
    expect(pager.rows().length).toBe(35);
    printing.active.set(false);
    expect(pager.rows().length).toBe(10);
  });
});

describe('pageItems', () => {
  it('lists every page up to seven', () => {
    expect(pageItems(0, 1)).toEqual([0]);
    expect(pageItems(3, 7)).toEqual([0, 1, 2, 3, 4, 5, 6]);
  });

  it('keeps the first, the last and the neighbours of the current page, with gaps', () => {
    expect(pageItems(0, 20)).toEqual([0, 1, 2, 3, null, 19]);
    expect(pageItems(10, 20)).toEqual([0, null, 9, 10, 11, null, 19]);
    expect(pageItems(19, 20)).toEqual([0, null, 16, 17, 18, 19]);
    // A gap would hide one page only: that page is shown instead.
    expect(pageItems(3, 20)).toEqual([0, 1, 2, 3, 4, null, 19]);
  });
});

@Component({
  standalone: true,
  imports: [ListFooterComponent],
  template: `<app-list-footer [pager]="pager" />`,
})
class HostComponent {
  readonly data = signal(rows(35));
  readonly pager = createPager(() => this.data(), { key: 'spec.footer' });
}

describe('ListFooterComponent with a pager', () => {
  it('offers 10, 20, 50 and 100 rows and turns the pager', () => {
    const fixture = TestBed.createComponent(HostComponent);
    fixture.detectChanges();
    const el: HTMLElement = fixture.nativeElement;
    const select = el.querySelector('select') as HTMLSelectElement;
    expect(Array.from(select.options).map((o) => o.value)).toEqual(['10', '20', '50', '100']);
    expect(select.value).toBe('10');
    expect(el.querySelector('[aria-current="page"]')?.textContent?.trim()).toBe('1');

    (el.querySelectorAll('.pager__num')[2] as HTMLButtonElement).click();
    fixture.detectChanges();
    expect(fixture.componentInstance.pager.index()).toBe(2);

    select.value = '50';
    select.dispatchEvent(new Event('change'));
    fixture.detectChanges();
    expect(fixture.componentInstance.pager.size()).toBe(50);
    expect(fixture.componentInstance.pager.rows().length).toBe(35);
    expect(el.querySelector('.pager')).toBeNull(); // one page: nothing to turn
  });

  it('shows no size choice for a list of ten rows or fewer', () => {
    const fixture = TestBed.createComponent(HostComponent);
    fixture.componentInstance.data.set(rows(8));
    fixture.detectChanges();
    expect(fixture.nativeElement.querySelector('select')).toBeNull();
  });

  it('has its words in all three languages', () => {
    for (const lang of ['fr', 'ar', 'en'] as const) {
      for (const key of ['ui.list.size', 'ui.list.page', 'ui.list.pageOf', 'ui.list.range', 'ui.list.prev', 'ui.list.next']) {
        expect(TRANSLATIONS[lang][key]).withContext(`${lang} ${key}`).toBeTruthy();
      }
    }
  });
});
