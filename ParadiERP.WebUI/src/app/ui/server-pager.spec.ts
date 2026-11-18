import { Component, signal } from '@angular/core';
import { TestBed, fakeAsync, flush, tick } from '@angular/core/testing';
import { PageQuery, PageResult, createServerPager, pageQueryString } from './server-pager';

type Filters = { q: string; kind: string };

/** A server of 35 rows numbered 1…35, filtered by `q` as a substring, that records what it was asked. */
function server() {
  const asked: PageQuery<Filters>[] = [];
  const answers: Array<(page: PageResult<number, { count: number }>) => void> = [];
  let manual = false;
  const all = Array.from({ length: 35 }, (_, i) => i + 1);
  const load = (query: PageQuery<Filters>): Promise<PageResult<number, { count: number }>> => {
    asked.push(query);
    const rows = all.filter((n) => !query.filters.q || String(n).includes(query.filters.q));
    const skip = rows.length === 0 ? 0 : query.skip < rows.length ? query.skip : Math.floor((rows.length - 1) / query.take) * query.take;
    const page = { items: rows.slice(skip, skip + query.take), total: rows.length, skip, take: query.take, summary: { count: rows.length } };
    if (manual) return new Promise((resolve) => answers.push(() => resolve(page)));
    return Promise.resolve(page);
  };
  return { asked, load, answers, hold: () => (manual = true) };
}

let currentLoad: (query: PageQuery<Filters>) => Promise<PageResult<number, { count: number }>>;

@Component({ standalone: true, template: '' })
class HostComponent {
  readonly q = signal('');
  readonly kind = signal('all');
  readonly pager = createServerPager<number, Filters, { count: number }>((query) => currentLoad(query), {
    filters: () => ({ q: this.q(), kind: this.kind() }),
    debounceMs: 300,
  });
}

function host(load: (query: PageQuery<Filters>) => Promise<PageResult<number, { count: number }>>) {
  currentLoad = load;
  const fixture = TestBed.createComponent(HostComponent);
  fixture.detectChanges();
  return fixture;
}

/** Server paging (owner, 2026-11-17: "paginate in backend, not frontend"). */
describe('createServerPager', () => {
  it('asks for ten rows first and every page it turns to', fakeAsync(() => {
    const s = server();
    const fixture = host(s.load);
    flush();
    const pager = fixture.componentInstance.pager;
    expect(s.asked[0]).toEqual({ skip: 0, take: 10, filters: { q: '', kind: 'all' } });
    expect(pager.rows()).toEqual([1, 2, 3, 4, 5, 6, 7, 8, 9, 10]);
    expect(pager.total()).toBe(35);
    expect(pager.summary()).toEqual({ count: 35 });

    pager.go(3);
    fixture.detectChanges();
    flush();
    expect(s.asked.at(-1)?.skip).toBe(30);
    expect(pager.rows()).toEqual([31, 32, 33, 34, 35]);

    pager.setSize(20);
    fixture.detectChanges();
    flush();
    expect(s.asked.at(-1)).toEqual(jasmine.objectContaining({ skip: 20, take: 20 }));
  }));

  it('goes back to page one when a filter changes, after the pause', fakeAsync(() => {
    const s = server();
    const fixture = host(s.load);
    flush();
    const pager = fixture.componentInstance.pager;
    pager.go(2);
    fixture.detectChanges();
    flush();

    fixture.componentInstance.q.set('3');
    fixture.detectChanges();
    tick(100);
    flush();
    expect(s.asked.at(-1)).toEqual({ skip: 0, take: 10, filters: { q: '3', kind: 'all' } });
    expect(pager.index()).toBe(0);
    expect(pager.total()).toBe(9); // 3, 13, 23, 30–35
  }));

  it('keeps only the answer to the last request', fakeAsync(() => {
    const s = server();
    s.hold();
    const fixture = host(s.load);
    const pager = fixture.componentInstance.pager;
    pager.go(1);
    fixture.detectChanges();
    tick();
    // The answer to page one arrives after the answer to page two: it is dropped.
    s.answers[1](null as never);
    tick();
    s.answers[0](null as never);
    tick();
    expect(pager.rows()[0]).toBe(11);
  }));

  it('shows the last page when the server answers with it, without asking again', fakeAsync(() => {
    const s = server();
    const fixture = host(s.load);
    flush();
    const pager = fixture.componentInstance.pager;
    pager.go(9); // past the end
    fixture.detectChanges();
    flush();
    fixture.detectChanges();
    flush();
    expect(pager.index()).toBe(3);
    expect(pager.rows()).toEqual([31, 32, 33, 34, 35]);
    expect(s.asked.length).toBe(2);
  }));

  it('sends only the filters that are set', () => {
    expect(pageQueryString({ skip: 0, take: 10, q: '', kind: 'all', balance: 'owes', id: null })).toBe('?skip=0&take=10&balance=owes');
  });
});
