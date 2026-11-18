import { signal } from '@angular/core';
import { TestBed } from '@angular/core/testing';
import { I18nService } from '../../../core/i18n.service';
import { RegionalService } from '../../../core/regional.service';
import { ConfirmService } from '../../../ui/confirm.service';
import { PayrollApi, PayrollSettings, PayrollSettingsView } from './payroll-api.service';
import { PayrollSettingsComponent } from './payroll-settings.component';

/**
 * The wiring between this screen and what the server actually sends.
 *
 * <p>Every defect this file pins was invisible to a type-check and to the server's own tests,
 * and all of them were the same shape: a name on one side that the other side does not use. The
 * enums crossed the wire as ordinals while the `<option value>`s were names, so nine selects
 * rendered empty and the first save was refused by the model binder; the ownership table crossed
 * it in PascalCase while the lookups were camelCase, so the income-tax mode, the tax-base flag
 * and both contribution bounds never rendered at all — although the server carried and owned
 * every one of them.</p>
 *
 * <p>So the fixture below is deliberately the server's JSON as it stands, not a hand-built
 * object in whatever shape the component would like.</p>
 */

/** The settings as `GET /api/hr/settings` serialises them: camelCase, enums by name. */
function settings(): PayrollSettings {
  return {
    countryCode: 'DZ',
    socialEmployeeRate: 10,
    socialEmployerRate: 30,
    socialNames: { fr: 'Sécurité sociale' },
    socialBaseFloor: null,
    socialBaseCeiling: null,
    employerLevies: [],
    incomeTaxMode: 'Scale',
    incomeTaxBrackets: [
      { upTo: 20000, rate: 0 },
      { upTo: null, rate: 20 },
    ],
    incomeTaxNames: { fr: 'IRG' },
    employeeContributionReducesTaxBase: true,
    minimumWage: 10000,
    allowances: [
      {
        key: 'panier',
        labels: { fr: 'Panier' },
        calculation: 'FixedAmount',
        value: 0,
        subjectToCnas: false,
        subjectToIrg: false,
        isActive: true,
        sortOrder: 1,
      },
    ],
    overtimeRules: [
      {
        key: 'hs-nuit',
        labels: { fr: 'Nuit' },
        appliesTo: 'Night',
        multiplier: 1.5,
        subjectToCnas: true,
        subjectToIrg: false,
        isActive: true,
      },
    ],
    deductions: [],
    defaultAttendanceMode: 'Declared',
    workingDaysPerMonth: 26,
    hoursPerDay: 8,
    confirmed: false,
    confirmedAtUtc: null,
    confirmedBy: null,
    needsReview: true,
  } as unknown as PayrollSettings;
}

/**
 * The ownership table exactly as the server sends it — camelCase, because System.Text.Json
 * renames properties and leaves dictionary keys alone and the server therefore converts these
 * itself (`PayrollOwnership.WireFields`).
 */
const owners: PayrollSettingsView['owners'] = {
  socialEmployeeRate: 'Operator',
  socialEmployerRate: 'Operator',
  socialNames: 'Operator',
  socialBaseFloor: 'Operator',
  socialBaseCeiling: 'Operator',
  employerLevies: 'Operator',
  incomeTaxMode: 'Operator',
  incomeTaxBrackets: 'Operator',
  incomeTaxNames: 'Operator',
  employeeContributionReducesTaxBase: 'Operator',
  minimumWage: 'Operator',
  confirmed: 'Operator',
  confirmedAtUtc: 'Operator',
  confirmedBy: 'Operator',
  needsReview: 'Operator',
  countryCode: 'Operator',
  allowances: 'Company',
  overtimeRules: 'Company',
  deductions: 'Company',
  defaultAttendanceMode: 'Company',
  workingDaysPerMonth: 'Company',
  hoursPerDay: 'Company',
};

function view(canEditStatutory = true): PayrollSettingsView {
  return { settings: settings(), revision: 4, owners, canEditCompany: true, canEditStatutory };
}

function mount(canEditStatutory = true) {
  const sent: { settings: PayrollSettings; revision: number }[] = [];
  TestBed.configureTestingModule({
    imports: [PayrollSettingsComponent],
    providers: [
      {
        provide: PayrollApi,
        useValue: {
          settings: () => Promise.resolve(view(canEditStatutory)),
          saveCompany: (s: PayrollSettings, revision: number) => {
            sent.push({ settings: s, revision });
            return Promise.resolve(view(canEditStatutory));
          },
          saveStatutory: (s: PayrollSettings, revision: number) => {
            sent.push({ settings: s, revision });
            return Promise.resolve(view(canEditStatutory));
          },
          confirmStatutory: () => Promise.resolve(view(canEditStatutory)),
        },
      },
      {
        provide: I18nService,
        useValue: { t: (key: string) => key, lang: signal('fr'), dateTime: () => '', date: () => '' },
      },
      { provide: RegionalService, useValue: { moneyStep: () => 1 } },
      { provide: ConfirmService, useValue: { ask: () => Promise.resolve(true) } },
    ],
  });
  return sent;
}

/**
 * Two settle rounds, not one. `NgModel` writes the loaded value — and the disabled state — into
 * the DOM control on a resolved promise of its own, so a single `detectChanges` after the load
 * leaves every select still showing the empty option and reads the wrong `disabled`.
 */
async function render() {
  const fixture = TestBed.createComponent(PayrollSettingsComponent);
  fixture.detectChanges();
  await fixture.whenStable();
  fixture.detectChanges();
  await fixture.whenStable();
  fixture.detectChanges();
  return fixture;
}

describe('the payroll settings screen against the server it talks to', () => {
  it('renders every statutory control the server says it owns', async () => {
    mount();
    const fixture = await render();

    const dom: HTMLElement = fixture.nativeElement;
    // These four were absent from the DOM while the server carried and owned all four: the
    // ownership lookup missed on every key, so the groups they sit in never opened.
    expect(dom.querySelector('#ps-taxmode')).withContext('income-tax mode').not.toBeNull();
    expect(dom.querySelector('#ps-tb')).withContext('contribution off the tax base').not.toBeNull();
    expect(dom.querySelector('#ps-floor')).withContext('contribution base floor').not.toBeNull();
    expect(dom.querySelector('#ps-ceiling')).withContext('contribution base ceiling').not.toBeNull();
  });

  it('binds every enum-valued control to the value that arrived', async () => {
    mount();
    const fixture = await render();

    const dom: HTMLElement = fixture.nativeElement;
    // An ordinal on the wire leaves each of these showing nothing at all, so the admin cannot
    // read what is set — and the first pick he makes is then refused by the model binder.
    expect((dom.querySelector('#ps-mode') as HTMLSelectElement).value).toBe('Declared');
    expect((dom.querySelector('#ps-taxmode') as HTMLSelectElement).value).toBe('Scale');

    const selects = Array.from(dom.querySelectorAll('select')) as HTMLSelectElement[];
    expect(selects.length).toBeGreaterThan(2);
    for (const select of selects) expect(select.value).not.toBe('');
  });

  it('gives an overtime band the two base flags the engine reads', async () => {
    mount();
    const fixture = await render();

    // The band's own answer, not the server's default: a company whose agreement exempts night
    // hours from the tax base had nowhere on this screen to say so.
    const screen = fixture.componentInstance;
    expect(screen.draft().overtimeRules[0].subjectToIrg).toBe(false);

    screen.addOvertime();
    const added = screen.draft().overtimeRules[1];
    expect(added.subjectToCnas).toBe(true);
    expect(added.subjectToIrg).toBe(true);
  });

  it('says in words that the figures are unconfirmed, rather than hiding them', async () => {
    mount(false);
    const fixture = await render();

    const dom: HTMLElement = fixture.nativeElement;
    expect(dom.textContent).toContain('hr.settings.provisional.title');
    // Shown and disabled, never removed: a control that vanishes teaches nobody that the figure
    // exists or who to ask for it.
    expect(dom.querySelector('#ps-taxmode')).not.toBeNull();
    expect((dom.querySelector('#ps-taxmode') as HTMLSelectElement).disabled).toBe(true);
    expect(dom.textContent).toContain('hr.settings.statutoryReadOnly');
  });

  it('marks invalid statutory fields, explains why, and disables statutory save', async () => {
    mount();
    const fixture = await render();
    const screen = fixture.componentInstance;
    const dom: HTMLElement = fixture.nativeElement;

    const findStatutorySave = () =>
      Array.from(dom.querySelectorAll<HTMLButtonElement>('button')).find((b) =>
        b.textContent?.includes('hr.settings.saveStatutory')
      );

    const minInput = dom.querySelector<HTMLInputElement>('#ps-min')!;
    const minField = minInput.closest('.bo-field')!;
    const statutorySave = findStatutorySave()!;

    // With valid values neither shows, and statutory Save is enabled
    expect(minField.classList.contains('is-invalid')).toBe(false);
    expect(dom.textContent).not.toContain('hr.settings.notNegative');
    expect(dom.textContent).not.toContain('hr.settings.bracketRateRange');
    expect(dom.querySelector('input[aria-invalid="true"]')).toBeNull();
    expect(statutorySave.disabled).toBe(false);

    // With a negative minimum wage the field has .is-invalid and the message shows; Save is disabled
    screen.setNumber('minimumWage', -1);
    fixture.detectChanges();
    await fixture.whenStable();
    fixture.detectChanges();
    expect(minField.classList.contains('is-invalid')).toBe(true);
    expect(dom.textContent).toContain('hr.settings.notNegative');
    expect(statutorySave.disabled).toBe(true);

    // Restore to valid: neither shows, and Save is re-enabled
    screen.setNumber('minimumWage', 10000);
    fixture.detectChanges();
    await fixture.whenStable();
    fixture.detectChanges();
    expect(minField.classList.contains('is-invalid')).toBe(false);
    expect(dom.textContent).not.toContain('hr.settings.notNegative');
    expect(statutorySave.disabled).toBe(false);

    // With a bracket rate of 120 its input has aria-invalid="true" and the bracket message shows; Save is disabled
    const firstBracket = screen.draft().incomeTaxBrackets[0];
    screen.setRowNumber(firstBracket, 'rate', 120);
    fixture.detectChanges();
    await fixture.whenStable();
    fixture.detectChanges();

    const invalidBracketInput = dom.querySelector<HTMLInputElement>('input[aria-invalid="true"]');
    expect(invalidBracketInput).not.toBeNull();
    expect(invalidBracketInput?.value).toBe('120');
    expect(invalidBracketInput?.classList.contains('is-invalid')).toBe(true);
    expect(dom.textContent).toContain('hr.settings.bracketRateRange');
    expect(statutorySave.disabled).toBe(true);

    // Restore bracket rate to valid: neither shows, and Save is re-enabled
    screen.setRowNumber(firstBracket, 'rate', 0);
    fixture.detectChanges();
    await fixture.whenStable();
    fixture.detectChanges();
    expect(dom.querySelector('input[aria-invalid="true"]')).toBeNull();
    expect(dom.textContent).not.toContain('hr.settings.bracketRateRange');
    expect(statutorySave.disabled).toBe(false);

    // Under each social rate field, when rateInvalid is true, the message shows
    screen.setNumber('socialEmployeeRate', 120);
    fixture.detectChanges();
    await fixture.whenStable();
    fixture.detectChanges();
    expect(dom.textContent).toContain('hr.settings.rateRange');
    expect(statutorySave.disabled).toBe(true);

    screen.setNumber('socialEmployeeRate', 10);
    fixture.detectChanges();
    await fixture.whenStable();
    fixture.detectChanges();
    expect(dom.textContent).not.toContain('hr.settings.rateRange');
    expect(statutorySave.disabled).toBe(false);
  });

  it('sends netPayOnly and overtime rule units when saving the company', async () => {
    const sent = mount();
    const fixture = await render();
    const screen = fixture.componentInstance;

    screen.setNetPayOnly(true);
    screen.setUnit(screen.draft().overtimeRules[0], 'Days');
    await screen.saveCompany();

    expect(sent.length).toBe(1);
    expect(sent[0].settings.netPayOnly).toBe(true);
    expect(sent[0].settings.overtimeRules[0].unit).toBe('Days');
  });
});

