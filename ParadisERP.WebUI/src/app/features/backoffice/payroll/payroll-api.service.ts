import { Injectable, inject } from '@angular/core';
import { ApiService } from '../../../core/api.service';

/**
 * The personnel module's wire types and its one client.
 *
 * <p>Written against `ParadisERP.API/Endpoints/HrEndpoints.cs` and
 * `ParadisERP.Application/Contracts/PayrollSettingsContracts.cs` — what the server actually
 * returns. This part carries the settings only; employees, attendance and payroll runs land
 * beside it later.</p>
 *
 * <p>It lives beside the screens rather than in `core/` so the whole module stays inside the
 * deferred chunk and never reaches a till's initial download. Nothing under `/api/hr` has a
 * reader on a cashier's device.</p>
 *
 * <p>`settings` is the engine's own settings object rather than a flattened copy: a figure a
 * later finance law adds arrives here without a change to this file, and a figure this file
 * does not name is still sent back untouched on a save.</p>
 */

export type PayBasis = 'Daily' | 'Monthly';
export type AttendanceMode = 'Recorded' | 'Declared';
export type AllowanceCalculation = 'FixedAmount' | 'PercentOfBase';
export type OvertimeBand = 'Weekday' | 'Night' | 'WeeklyRest' | 'PublicHoliday';
/** Who owns a figure. Not a permission — it is what the screen groups by and explains. */
export type PayrollFieldOwner = 'Operator' | 'Company';

/** Per-language labels, keyed by language code. */
export type TextByLang = Record<string, string>;

export interface AllowanceDef {
  key: string;
  labels: TextByLang;
  calculation: AllowanceCalculation;
  /** An amount, or a percentage of the base wage, per `calculation`. */
  value: number;
  /** Whether it enters the contribution base. The accountant's answer, never a default. */
  subjectToCnas: boolean;
  /** Whether it enters the income-tax base. */
  subjectToIrg: boolean;
  isActive: boolean;
  sortOrder: number;
}

export interface OvertimeRuleDef {
  key: string;
  labels: TextByLang;
  appliesTo: OvertimeBand;
  /** What an overtime band counts. */
  unit?: 'Hours' | 'Days';
  /** Multiplier on the hourly rate; 1 means no premium has been set yet. */
  multiplier: number;
  /**
   * Whether these hours enter the contribution base — the same explicit answer an allowance
   * carries, and for the same reason: the engine reads it and decides nothing of its own. These
   * were missing here, so a band the admin added came back with the server's defaults (both
   * true) and no screen could ever say otherwise; a company whose agreement exempts night hours
   * from one base had nowhere to express it.
   */
  subjectToCnas: boolean;
  /** Whether they enter the income-tax base. */
  subjectToIrg: boolean;
  isActive: boolean;
}

/** One step of the scale. `upTo: null` is the open-ended top step. */
export interface TaxBracket {
  upTo: number | null;
  rate: number;
}

export interface WorkSchedulePeriod {
  name: string;
  from: string;
  to: string;
  start: string;
  end: string;
  breakMinutes: number;
}

export interface WorkSchedule {
  start: string;
  end: string;
  breakMinutes: number;
  lateGraceMinutes: number;
  /** DayOfWeek numbers, Sunday = 0 … Saturday = 6 */
  restDays: number[];
  periods: WorkSchedulePeriod[];
}

/**
 * Everything the engine computes against. Extra properties the server adds are preserved on a
 * save because the object is round-tripped, not rebuilt.
 */
export interface PayrollSettings {
  countryCode: string;
  socialEmployeeRate: number;
  socialEmployerRate: number;
  socialNames: TextByLang;
  incomeTaxBrackets: TaxBracket[];
  incomeTaxNames: TextByLang;
  minimumWage: number;
  allowances: AllowanceDef[];
  overtimeRules: OvertimeRuleDef[];
  defaultAttendanceMode: AttendanceMode;
  workingDaysPerMonth: number;
  hoursPerDay: number;
  schedule?: WorkSchedule;
  /** An unpaid absent day comes off the gross (base and allowances), not the base alone (answer 40). */
  absenceOffGross?: boolean;
  /** Whether a workers' bonus paid by payment voucher counts in the contribution / income-tax base (owner, 2026-11-08). */
  voucherBonusInSocialBase?: boolean;
  voucherBonusInTaxBase?: boolean;
  /** Net pay only: no allowances, no social contribution, no income tax on the payslip. */
  netPayOnly?: boolean;
  /** Set once somebody with the current law in front of them said these figures are right. */
  confirmed: boolean;
  confirmedAtUtc?: string | null;
  confirmedBy?: string | null;
  /**
   * `confirmed` read the other way round — the server computes it, so it is never sent as a
   * second opinion. True while the statutory figures are still the unconfirmed placeholders.
   */
  needsReview: boolean;
  [extra: string]: unknown;
}

export interface PayrollSettingsView {
  settings: PayrollSettings;
  /** Send it back on a save; a mismatch means somebody else saved first. */
  revision: number;
  /**
   * Field name to `Operator` or `Company`, from the server's own ownership table, keyed the way
   * every other property of this payload is keyed — camel case. The server converts it
   * explicitly (`PayrollOwnership.WireFields`), because System.Text.Json renames properties and
   * leaves dictionary keys alone.
   */
  owners: Record<string, PayrollFieldOwner>;
  canEditCompany: boolean;
  canEditStatutory: boolean;
  /** Who last saved. Who confirmed is `settings.confirmedBy` — a different fact. */
  updatedBy?: string | null;
  updatedAtUtc?: string | null;
}

@Injectable({ providedIn: 'root' })
export class PayrollApi {
  private readonly api = inject(ApiService);

  settings(): Promise<PayrollSettingsView> {
    return this.api.getJson<PayrollSettingsView>('/api/hr/settings');
  }

  /** The company's own half. A statutory figure in the body is refused by name. */
  saveCompany(settings: PayrollSettings, revision: number): Promise<PayrollSettingsView> {
    return this.api.putJson<PayrollSettingsView>('/api/hr/settings/company', { settings, revision });
  }

  /**
   * The statutory half. The server reads only the statutory fields out of what is sent, so the
   * whole settings object may be passed without risk of carrying a company change along.
   */
  saveStatutory(
    settings: PayrollSettings,
    revision: number,
    reviewed = false,
    confirmedBy?: string | null,
  ): Promise<PayrollSettingsView> {
    return this.api.putJson<PayrollSettingsView>('/api/hr/settings/statutory', {
      settings,
      revision,
      reviewed,
      confirmedBy: confirmedBy ?? null,
    });
  }

  /** "Confirmed by our accountant", with no figure changed. */
  confirmStatutory(revision: number, confirmedBy?: string | null): Promise<PayrollSettingsView> {
    return this.api.postJson<PayrollSettingsView>('/api/hr/settings/statutory/confirm', {
      revision,
      confirmedBy: confirmedBy ?? null,
    });
  }
}
