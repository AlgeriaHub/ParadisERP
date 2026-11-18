import { Component, signal } from '@angular/core';
import { ComponentFixture, TestBed, fakeAsync, tick } from '@angular/core/testing';
import { MENU } from './menu.component';

/** A row's ⋯ inside a clipping, scrolling box — the products table's shape. */
@Component({
  standalone: true,
  imports: [MENU],
  template: `
    <div [attr.dir]="dir()" class="scroller" [style.margin-top.px]="offset()"
         style="overflow: auto; inline-size: 300px; block-size: 48px; margin-left: 200px">
      <div style="display: flex; justify-content: flex-end; block-size: 40px">
        <app-menu label="More">
          <span menuTrigger>⋯</span>
          <button appMenuItem type="button">Edit</button>
          <button appMenuItem type="button">Duplicate</button>
          <button appMenuItem type="button">Delete</button>
        </app-menu>
      </div>
    </div>
  `,
})
class RowHost {
  readonly dir = signal<'ltr' | 'rtl'>('ltr');
  readonly offset = signal(0);
}

describe('MenuComponent placement', () => {
  let fixture: ComponentFixture<RowHost>;
  const q = (s: string) => fixture.nativeElement.querySelector(s) as HTMLElement;

  function open(): void {
    q('.trigger').click();
    fixture.detectChanges();
    tick();
    fixture.detectChanges();
  }

  beforeEach(() => {
    TestBed.configureTestingModule({ imports: [RowHost] });
    fixture = TestBed.createComponent(RowHost);
    // At the top of the page, not after the Karma HTML reporter: its results grow by one mark per
    // spec run, and late in a random order they pushed the row low enough that the panel had no
    // room below and rightly opened upward — failing the downward case for reasons of its own.
    document.body.prepend(fixture.nativeElement);
    // And with the page at its top: a spec that ran earlier may have scrolled it (a field brought
    // into view), and the placement is measured against the viewport. Left scrolled, the row of
    // the upward case sat above the screen, the panel found room below and rightly opened downward
    // (the deploy of 2026-10-15: "Expected -445 to be less than or equal -631").
    window.scrollTo(0, 0);
    fixture.detectChanges();
  });

  afterEach(() => fixture.nativeElement.remove());

  it('floats the panel outside a clipping scroll box', fakeAsync(() => {
    open();
    const panel = q('.panel');
    const trigger = q('.trigger').getBoundingClientRect();
    const box = panel.getBoundingClientRect();
    const scroller = q('.scroller').getBoundingClientRect();
    expect(getComputedStyle(panel).position).toBe('fixed');
    expect(getComputedStyle(panel).opacity).toBe('1');
    expect(box.top).toBeGreaterThanOrEqual(trigger.bottom);
    // Taller than the 48px box it sits in, and not cut to it.
    expect(box.bottom).toBeGreaterThan(scroller.bottom);
    expect(document.elementFromPoint(box.left + 10, box.bottom - 10)?.closest('.panel')).toBe(panel);
  }));

  it('lines up with the trigger end edge in LTR and mirrors in RTL', fakeAsync(() => {
    open();
    let trigger = q('.trigger').getBoundingClientRect();
    let box = q('.panel').getBoundingClientRect();
    expect(Math.round(box.right)).toBe(Math.round(trigger.right));
    q('.trigger').click();
    fixture.detectChanges();

    fixture.componentInstance.dir.set('rtl');
    fixture.detectChanges();
    open();
    trigger = q('.trigger').getBoundingClientRect();
    box = q('.panel').getBoundingClientRect();
    expect(Math.round(box.left)).toBe(Math.round(trigger.left));
  }));

  it('opens upward when there is no room below', fakeAsync(() => {
    fixture.componentInstance.offset.set(document.documentElement.clientHeight - 60);
    fixture.detectChanges();
    const at = q('.trigger').getBoundingClientRect();
    // The case needs the row on screen, near its bottom edge; say so if it is not, rather than
    // failing on the panel.
    expect(at.top).withContext('the trigger is on screen').toBeGreaterThanOrEqual(0);
    expect(at.bottom).withContext('the trigger is on screen').toBeLessThanOrEqual(document.documentElement.clientHeight);
    open();
    const trigger = q('.trigger').getBoundingClientRect();
    const box = q('.panel').getBoundingClientRect();
    expect(box.bottom).toBeLessThanOrEqual(trigger.top);
  }));

  it('still closes on Escape and returns focus to the trigger', fakeAsync(() => {
    open();
    expect(document.activeElement?.textContent).toBe('Edit');
    q('.panel').dispatchEvent(new KeyboardEvent('keydown', { key: 'Escape', bubbles: true }));
    fixture.detectChanges();
    expect(q('.panel').hidden).toBeTrue();
    expect(document.activeElement).toBe(q('.trigger'));
  }));
});
