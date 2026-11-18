import { signal } from '@angular/core';
import { TestBed } from '@angular/core/testing';
import { followSaleSync } from './follow-sale-sync';
import { Sale } from './models';

describe('followSaleSync — the till\'s finished sale follows its sync', () => {
  const pending = { id: 's1', reference: 'T-0001', outboxState: 'pending' } as Sale;
  const sending = { ...pending, outboxState: 'sending' } as Sale;
  const synced = { ...pending, outboxState: 'synced' } as Sale;

  let held: ReturnType<typeof signal<Sale | null>>;
  let pushed: ReturnType<typeof signal<number>>;
  let read: jasmine.Spy<(id: string) => Promise<Sale | undefined>>;

  beforeEach(() => {
    held = signal<Sale | null>(null);
    pushed = signal(0);
    read = jasmine.createSpy('read').and.resolveTo(pending);
    TestBed.runInInjectionContext(() => followSaleSync(held, read, pushed));
  });

  /** Lets the effect run and the read settle. */
  async function settle(): Promise<void> {
    TestBed.tick();
    for (let i = 0; i < 3; i++) await Promise.resolve();
  }

  it('reads the held sale again after each push until it is synced, then leaves it alone', async () => {
    held.set(pending);
    await settle();
    expect(read).toHaveBeenCalledWith('s1');
    expect(held()).toBe(pending);

    read.and.resolveTo(synced);
    pushed.set(1);
    await settle();
    expect(held()!.outboxState).toBe('synced');

    read.calls.reset();
    pushed.set(2);
    await settle();
    expect(read).not.toHaveBeenCalled();
  });

  it('takes the synced sale at once when its push finished before the screen held it', async () => {
    read.and.resolveTo(synced);
    held.set(pending);
    await settle();
    expect(held()).toEqual(synced);
  });

  it('holds nothing again once the next customer has started', async () => {
    let release!: (sale: Sale) => void;
    read.and.returnValue(new Promise<Sale>((resolve) => (release = resolve)));
    held.set(pending);
    await settle();
    held.set(null);
    release(synced);
    await settle();
    expect(held()).toBeNull();
  });

  it('never lets a slow read put back an older state', async () => {
    const reads: ((sale: Sale) => void)[] = [];
    read.and.callFake(() => new Promise<Sale>((resolve) => reads.push(resolve)));
    held.set(pending);
    await settle();
    // A push while the first read is still out: a second read of the same held sale.
    pushed.set(1);
    await settle();
    expect(reads.length).toBe(2);
    reads[1](sending);
    await settle();
    expect(held()!.outboxState).toBe('sending');
    // The first read answers late, with what was true before: it no longer matches the held sale.
    reads[0](sending);
    await settle();
    expect(held()!.outboxState).toBe('sending');
    expect(held()).not.toBe(pending);
  });
});
