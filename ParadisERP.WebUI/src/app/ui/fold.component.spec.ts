import { Component } from '@angular/core';
import { ComponentFixture, TestBed } from '@angular/core/testing';
import { FoldComponent } from './fold.component';
import { revealField } from './reveal-field';

@Component({
  standalone: true,
  imports: [FoldComponent],
  template: `
    <app-fold heading="Contrat de location">
      <span foldState class="state">2 changes left</span>
      <textarea id="body-field"></textarea>
    </app-fold>
  `,
})
class FormHost {}

/** PinSay #303: a form section that folds, with its state kept in view while folded. */
describe('FoldComponent', () => {
  let fixture: ComponentFixture<FormHost>;
  const q = (s: string) => fixture.nativeElement.querySelector(s) as HTMLElement;

  beforeEach(() => {
    TestBed.configureTestingModule({ imports: [FormHost] });
    fixture = TestBed.createComponent(FormHost);
    fixture.detectChanges();
  });

  it('opens folded, with the fields hidden but still in the page', () => {
    const button = q('h3 button');
    expect(button.getAttribute('aria-expanded')).toBe('false');
    expect(button.textContent).toContain('Contrat de location');
    expect(button.textContent).toContain('+');
    const body = q('#' + button.getAttribute('aria-controls'));
    expect(body.hidden).toBeTrue();
    expect(body.contains(q('#body-field'))).toBeTrue();
  });

  it('keeps the state beside the heading, outside the part that folds', () => {
    const state = q('.state');
    expect(state.closest('.fold__body')).toBeNull();
    expect(state.textContent).toContain('2 changes left');
  });

  it('opens and closes on its heading', () => {
    const button = q('h3 button');
    button.click();
    fixture.detectChanges();
    expect(button.getAttribute('aria-expanded')).toBe('true');
    expect(button.textContent).toContain('−');
    expect(q('.fold__body').hidden).toBeFalse();
    button.click();
    fixture.detectChanges();
    expect(q('.fold__body').hidden).toBeTrue();
  });

  it('preserves a draft through collapse and reopens it for a validation link', () => {
    const field = q('#body-field') as HTMLTextAreaElement;
    q('h3 button').click();
    fixture.detectChanges();
    field.value = 'Unfinished contract';
    q('h3 button').click();
    fixture.detectChanges();
    revealField(field);
    expect(q('.fold__body').hidden).toBeFalse();
    expect(field.value).toBe('Unfinished contract');
    expect(document.activeElement).toBe(field);
  });

  it('reveals a hidden required field during native validation', () => {
    const field = q('#body-field') as HTMLTextAreaElement;
    field.required = true;
    expect(field.checkValidity()).toBeFalse();
    expect(q('.fold__body').hidden).toBeFalse();
  });

  it('reveals a newly marked validation error but allows it to be folded again', async () => {
    q('#body-field').setAttribute('aria-invalid', 'true');
    fixture.detectChanges();
    await fixture.whenStable();
    fixture.detectChanges();
    expect(q('.fold__body').hidden).toBeFalse();
    q('h3 button').click();
    fixture.detectChanges();
    await fixture.whenStable();
    expect(q('.fold__body').hidden).toBeTrue();
  });
});

@Component({
  standalone: true,
  imports: [FoldComponent],
  template: `
    <app-fold class="row" heading="Identité fiscale" [open]="true">
      <p foldLead class="lead">Ce que la loi demande sur chaque document.</p>
      <input id="nif" required />
    </app-fold>
  `,
})
class RowHost {}

/** 2026-10-08: a long form opens where the work starts, and a folded setting row keeps its note. */
describe('FoldComponent opened by its form', () => {
  let fixture: ComponentFixture<RowHost>;
  const q = (s: string) => fixture.nativeElement.querySelector(s) as HTMLElement;

  beforeEach(() => {
    TestBed.configureTestingModule({ imports: [RowHost] });
    fixture = TestBed.createComponent(RowHost);
    fixture.detectChanges();
  });

  it('starts open when the form says so, with its required field in view', () => {
    expect(q('h3 button').getAttribute('aria-expanded')).toBe('true');
    expect(q('.fold__body').hidden).toBeFalse();
    expect(q('#nif').closest('.fold__body')).not.toBeNull();
  });

  it('keeps the row explanation in view when the reader folds it', () => {
    q('h3 button').click();
    fixture.detectChanges();
    expect(q('.fold__body').hidden).toBeTrue();
    expect(q('.lead').closest('.fold__body')).toBeNull();
    expect(q('.lead').textContent).toContain('Ce que la loi demande');
  });
});

