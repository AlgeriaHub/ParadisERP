import { IdentityPart, identityProblems } from '../../core/identity';
import {
  Component,
  ElementRef,
  Injector,
  OnInit,
  afterNextRender,
  computed,
  inject,
  input,
  output,
  signal,
  viewChild,
} from '@angular/core';
import { FormsModule } from '@angular/forms';
import {
  AdminCustomer,
  AdminService,
  AdminStore,
  CustomerAttachment,
  CustomerKind,
  IdDocumentType,
  Representative,
  UpsertCustomerRequest,
  adminErrorMessage,
  problemCode,
} from '../../core/admin.service';
import { isolate } from '../../core/bidi';
import { AuthService } from '../../core/auth.service';
import { BranchScopeService } from '../../core/branch-scope.service';
import { customerOtherFiscalIds } from '../../core/customer-fiscal-ids';
import { FiscalIdDefinition } from '../../core/models';
import { I18nService } from '../../core/i18n.service';
import { RegionalService } from '../../core/regional.service';
import { localDay } from '../../core/rentals';
import { formatDay } from './backoffice-format';
import { TouchedFields } from './backoffice-forms';
import { ConfirmService } from '../../ui/confirm.service';
import { MENU } from '../../ui/menu.component';
import { ListFooterComponent } from '../../ui/list-footer.component';
import { createServerPager } from '../../ui/server-pager';
import { PHONE_NOT_MOBILE, formatPhone, normalizePhone, phoneExample } from '../../core/phone';
import { TableStateComponent } from '../../ui/table-state.component';
import { PAGE_CHROME } from './shell/page-chrome';
import { actionRequest, deleteRequest, saveRequest } from '../../ui/confirm-requests';
import { FoldComponent } from '../../ui/fold.component';
import { DateInputComponent } from '../../ui/date-input.component';
import { CustomerOpeningBalanceComponent } from './customer-opening-balance.component';
import { OpeningParty, emptyOpeningDraft, openingDraftProblem, openingPayload } from '../../core/opening-balance';
import { localToday } from '../../core/identity';
import {
  ID_DOCUMENT_TYPES,
  idDocumentTypeKey,
  idDocumentTypeOrNull,
  kindLabelKey,
  kindProblem,
  needsRepresentative,
  representativeEndsOnSave,
  takeoverDayProblem,
  takeoverInstant,
} from './customer-kind';

/** The server's code for a tax number given to a natural person (CustomerRules.TaxIdCompanyOnly). */
const TAX_ID_COMPANY_ONLY = 'taxIdCompanyOnly';


/**
 * The system's resize length (`--t-slide` in styles.scss) in milliseconds, read where the element
 * sits, or 0 — no motion — when the reader asks for reduced motion or the token cannot be read.
 */
export function slideMs(element: Element): number {
  if (globalThis.matchMedia?.('(prefers-reduced-motion: reduce)').matches) return 0;
  const raw = getComputedStyle(element).getPropertyValue('--t-slide').trim();
  const value = parseFloat(raw);
  if (!Number.isFinite(value) || value <= 0) return 0;
  return raw.endsWith('ms') ? value : value * 1000;
}

/**
 * The fields typed into the form; the kind, the identity paper and the representative are
 * decided beside them. The code is not one of them: the server draws it (owner, 2026-10-05).
 */
type CustomerFields = Omit<
  UpsertCustomerRequest,
  | 'kind'
  | 'fiscalIds'
  | 'idDocumentNumber'
  | 'representative'
  | 'idDocumentType'
  | 'idDocumentIssuer'
  | 'idDocumentIssuedOn'
  | 'firstName'
  | 'familyName'
> & {
  /** A person's two name parts, typed; `fullName` is the one name of a company or an undecided row. */
  firstName: string;
  familyName: string;
};

/**
 * What a save sends as the customer's other fiscal identifiers: a company's typed values for the
 * profile's identifiers, trimmed (the server drops a blank one); nothing at all for anyone else —
 * the server clears a person's whatever is sent.
 */
export function customerFiscalIdsPayload(
  kind: CustomerKind,
  defs: readonly FiscalIdDefinition[],
  values: Readonly<Record<string, string | undefined>>,
): Record<string, string> {
  if (kind !== 'Company') return {};
  return Object.fromEntries(defs.map((d) => [d.key, (values[d.key] ?? '').trim()]));
}

@Component({
  selector: 'app-backoffice-customers',
  standalone: true,
  imports: [
    FormsModule,
    FoldComponent,
    CustomerOpeningBalanceComponent,
    DateInputComponent,
    ListFooterComponent,
    TableStateComponent,
    ...PAGE_CHROME,
    ...MENU,
  ],
  template: `
    <div class="bo-stack">
      <!-- Create-only (FR-1), the editor is all there is: its error is said inside it, below. -->
      @if ((error() || listError()) && !createOnly()) {
        <p class="notice danger" role="alert">{{ error() || listError() }}</p>
      }
      @if (notice()) {
        <p class="notice ok" role="status">{{ notice() }}</p>
      }

      @if (editorOpen()) {
        <div class="bo-backdrop" (click)="cancelEdit()"></div>
      }
      <!-- A new customer's dialog starts small, with the kind question alone, and grows into the
           editor once it is answered (owner, 2026-10-05; grow()). Every host — this page, the till,
           the rental contract — shows this one dialog; none wraps it in another. -->
      <section
        #editor
        class="bo-panel"
        [class.bo-edit-modal]="editorOpen()"
        [class.is-choosing]="editorOpen() && choosingKind()"
        [class.bo-editor-hidden]="!editorOpen()"
        [attr.role]="editorOpen() ? 'dialog' : null"
        [attr.aria-modal]="editorOpen() ? 'true' : null"
        [attr.aria-label]="editorOpen() ? (editingId() ? i18n.t('common.edit') : i18n.t('backoffice.customers.new')) : null"
        (keydown.escape)="editorOpen() && cancelEdit()"
      >
        <div class="bo-panel__head">
          <h2 class="bo-panel__title">{{ editingId() ? i18n.t('common.edit') : i18n.t('backoffice.customers.new') }}</h2>
          @if (anyRequired() && !choosingKind()) {
            <p class="bo-panel__note">{{ i18n.t('backoffice.customers.requiredHint') }}</p>
          }
        </div>
        <div class="bo-panel__body bo-form">
          @if (error() && createOnly()) {
            <p class="notice danger" role="alert">{{ error() }}</p>
          }
          @if (choosingKind()) {
            <!-- A new customer starts with this one question and nothing else (owner, 2026-10-05):
                 no default, no fields that rearrange once it is answered. Two toggle buttons like
                 the chips that replace them once one is chosen; each names itself and says what it
                 means. Only Cancel sits below them: there is nothing to save yet. -->
            <div class="kind-pick" role="group" aria-labelledby="cu-pick-title">
              <h3 class="bo-group__title" id="cu-pick-title">{{ i18n.t('cust.kind.pick.title') }}</h3>
              <div class="kind-pick__choices">
                <button
                  type="button" class="kind-pick__choice" id="cu-pick-person" aria-pressed="false"
                  aria-labelledby="cu-pick-person-name" aria-describedby="cu-pick-person-hint"
                  (click)="pickKind('Person')"
                >
                  <span class="kind-pick__name" id="cu-pick-person-name">{{ i18n.t('cust.kind.pick.person') }}</span>
                  <span class="kind-pick__hint" id="cu-pick-person-hint">{{ i18n.t('cust.kind.pick.personHint') }}</span>
                </button>
                <button
                  type="button" class="kind-pick__choice" id="cu-pick-company" aria-pressed="false"
                  aria-labelledby="cu-pick-company-name" aria-describedby="cu-pick-company-hint"
                  (click)="pickKind('Company')"
                >
                  <span class="kind-pick__name" id="cu-pick-company-name">{{ i18n.t('cust.kind.pick.company') }}</span>
                  <span class="kind-pick__hint" id="cu-pick-company-hint">{{ i18n.t('cust.kind.pick.companyHint') }}</span>
                </button>
              </div>
            </div>
          } @else {
          <!-- Open from the start: the kind and the name are what the dialog grew to ask for, and
               the cursor goes to the name as soon as it has grown (grow()). -->
          <app-fold class="bo-group" [heading]="i18n.t('common.group.identity')" [open]="true">
            <div class="bo-fields">
              <!-- The kind comes first: everything under it — which fields show, whether a
                   representative is asked for — follows from this one choice. "Kind not set"
                   is a state a migrated row is in, never a third button. -->
              <div class="bo-field bo-field--wide" [class.is-invalid]="kindError()">
                <label id="cu-kind-label">
                  {{ i18n.t('cust.kind') }}
                  @if (!editingId()) {
                    <span class="bo-req">{{ i18n.t('common.required') }}</span>
                  }
                </label>
                <div class="bo-states">
                  <nav class="bo-chips" [attr.aria-label]="i18n.t('cust.kind')">
                    <button
                      type="button"
                      [class.is-on]="kind() === 'Person'"
                      [attr.aria-pressed]="kind() === 'Person'"
                      (click)="chooseKind('Person')"
                    >
                      {{ i18n.t('cust.kind.person') }}
                    </button>
                    <button
                      type="button"
                      [class.is-on]="kind() === 'Company'"
                      [attr.aria-pressed]="kind() === 'Company'"
                      (click)="chooseKind('Company')"
                    >
                      {{ i18n.t('cust.kind.company') }}
                    </button>
                  </nav>
                  @if (kindUnset()) {
                    <span class="bo-state bo-state--warn">{{ i18n.t('cust.kind.unset') }}</span>
                  }
                </div>
                <p class="bo-hint" id="cu-kind-hint">{{ i18n.t(kindUnset() ? 'cust.kind.unsetHint' : 'cust.kind.hint') }}</p>
                @if (kindError()) {
                  <span id="cu-kind-error" class="bo-error" role="alert">{{ i18n.t('cust.kind.required') }}</span>
                }
              </div>
              <!-- The code is never typed (owner, 2026-10-05): the server draws it when Save is
                   pressed — the kind's prefix and a dash, the branch's three-digit code, the year and
                   month, a sequence that restarts each month — and it never changes after. Nothing
                   here predicts or reserves the next number, so two people creating at once never
                   collide; the real code is shown once the save has answered. Text, not a control:
                   there is nothing to choose. -->
              <div class="bo-field">
                <p class="bo-fixed-label">{{ i18n.t('backoffice.customers.code') }}</p>
                @if (editing(); as row) {
                  <p class="bo-fixed mono" id="cu-code-fixed"><span dir="ltr">{{ row.code }}</span></p>
                } @else {
                  <p class="bo-fixed code-new" id="cu-code-new">
                    {{ i18n.t('cust.code.onSave') }}
                    @if (codePatternKey(); as key) {
                      <bdi class="mono" dir="ltr">{{ i18n.t(key) }}</bdi>
                    }
                  </p>
                  <p class="bo-hint" id="cu-code-hint">{{ i18n.t('cust.code.shape') }}</p>
                }
              </div>
              <!-- A person's name in two parts (owner, 2026-10-06: "الاسم واللقب" in separate inputs,
                   as the employee form has them), in the order it prints: first name, then family
                   name. A company has one name, its legal name, and an undecided row keeps the one
                   it was filed with. -->
              @if (kind() === 'Person') {
                <div class="bo-field" [class.is-invalid]="touched.missing('firstName', form.firstName)">
                  <label for="cu-first-name">
                    {{ i18n.t('cust.firstName') }}<span class="bo-req">{{ i18n.t('common.required') }}</span>
                  </label>
                  <input
                    id="cu-first-name" maxlength="100" [(ngModel)]="form.firstName"
                    [attr.aria-invalid]="touched.missing('firstName', form.firstName) ? 'true' : null"
                    [attr.aria-describedby]="touched.missing('firstName', form.firstName) ? 'cu-first-name-error' : null"
                    (blur)="touched.touch('firstName')"
                  />
                  @if (touched.missing('firstName', form.firstName)) {
                    <span id="cu-first-name-error" class="bo-error" role="alert">{{ i18n.t('common.fieldRequired') }}</span>
                  }
                  <!-- Filed before the split (or until now not a person): the whole name sits under
                       the family name until somebody moves the first name out — said here, where that
                       is done. -->
                  @if (nameToSplit()) {
                    <span class="bo-hint" id="cu-split-name">{{ i18n.t('cust.splitName') }}</span>
                  }
                </div>
                <div class="bo-field" [class.is-invalid]="touched.missing('familyName', form.familyName)">
                  <label for="cu-family-name">
                    {{ i18n.t('cust.familyName') }}<span class="bo-req">{{ i18n.t('common.required') }}</span>
                  </label>
                  <input
                    id="cu-family-name" maxlength="200" [(ngModel)]="form.familyName"
                    [attr.aria-invalid]="touched.missing('familyName', form.familyName) ? 'true' : null"
                    [attr.aria-describedby]="touched.missing('familyName', form.familyName) ? 'cu-family-name-error' : null"
                    (blur)="touched.touch('familyName')"
                  />
                  @if (touched.missing('familyName', form.familyName)) {
                    <span id="cu-family-name-error" class="bo-error" role="alert">{{ i18n.t('common.fieldRequired') }}</span>
                  }
                </div>
              } @else {
                <div class="bo-field" [class.is-invalid]="touched.missing('fullName', form.fullName)">
                  <label for="cu-name">
                    {{ i18n.t('backoffice.customers.fullName') }}<span class="bo-req">{{ i18n.t('common.required') }}</span>
                  </label>
                  <input
                    id="cu-name" maxlength="200" [(ngModel)]="form.fullName"
                    [attr.aria-invalid]="touched.missing('fullName', form.fullName) ? 'true' : null"
                    [attr.aria-describedby]="touched.missing('fullName', form.fullName) ? 'cu-name-error' : null"
                    (blur)="touched.touch('fullName')"
                  />
                  @if (touched.missing('fullName', form.fullName)) {
                    <span id="cu-name-error" class="bo-error" role="alert">{{ i18n.t('common.fieldRequired') }}</span>
                  }
                  @if (kind() === 'Company') {
                    <p class="bo-hint">{{ i18n.t('cust.legalNameHint') }}</p>
                  }
                </div>
              }
              @if (showsIdDocument()) {
                <div class="bo-field">
                  <label for="cu-id-type">
                    {{ i18n.t('idDoc.type') }}<span class="bo-req">{{ i18n.t('common.optional') }}</span>
                  </label>
                  <select id="cu-id-type" [ngModel]="idDocumentType()" (ngModelChange)="idDocumentType.set($event)">
                    <option value="">{{ i18n.t('idDoc.type.none') }}</option>
                    @for (type of idTypes; track type) {
                      <option [value]="type">{{ i18n.t(idTypeKey(type)) }}</option>
                    }
                  </select>
                </div>
                <div class="bo-field" [class.is-invalid]="!!ownIdProblems().idNumber">
                  <label for="cu-id-doc">
                    {{ i18n.t('cust.idDocument') }}<span class="bo-req">{{ i18n.t('common.optional') }}</span>
                  </label>
                  <input
                    id="cu-id-doc" class="mono" dir="ltr" maxlength="40"
                    [ngModel]="idDocumentNumber()"
                    (ngModelChange)="idDocumentNumber.set($event)"
                    aria-describedby="cu-id-doc-hint"
                  />
                  @if (ownIdProblems().idNumber; as key) {
                    <span class="bo-error" role="alert">{{ i18n.t(key) }}</span>
                  }
                  <p class="bo-hint" id="cu-id-doc-hint">{{ i18n.t('cust.idDocumentHint') }}</p>
                </div>
                <div class="bo-field" [class.is-invalid]="!!ownIdProblems().idIssuer">
                  <label for="cu-id-issuer">
                    {{ i18n.t('idDoc.issuer') }}<span class="bo-req">{{ i18n.t('common.optional') }}</span>
                  </label>
                  <input
                    id="cu-id-issuer" maxlength="120"
                    [ngModel]="idDocumentIssuer()"
                    (ngModelChange)="idDocumentIssuer.set($event)"
                    aria-describedby="cu-id-issuer-hint"
                  />
                  @if (ownIdProblems().idIssuer; as key) {
                    <span class="bo-error" role="alert">{{ i18n.t(key) }}</span>
                  }
                  <p class="bo-hint" id="cu-id-issuer-hint">{{ i18n.t('idDoc.issuerHint') }}</p>
                </div>
                <div class="bo-field" [class.is-invalid]="!!ownIdProblems().idIssuedOn">
                  <label for="cu-id-issued">
                    {{ i18n.t('idDoc.issuedOn') }}<span class="bo-req">{{ i18n.t('common.optional') }}</span>
                  </label>
                  <app-date-input
                    id="cu-id-issued"
                    [max]="todayDay()"
                    [ngModel]="idDocumentIssuedOn()"
                    (ngModelChange)="idDocumentIssuedOn.set($event ?? '')"
                  />
                  @if (ownIdProblems().idIssuedOn; as key) {
                    <span class="bo-error" role="alert">{{ i18n.t(key) }}</span>
                  }
                </div>
              }
              <!-- "الرقم الجبائي للشركات فقط" (owner, 2026-10-05): the tax number is a company's; a
                   natural person has none, so a person is not asked for one. A row whose kind
                   nobody has set yet keeps the field as it had it. -->
              @if (showsTaxId()) {
                <div class="bo-field" [class.is-invalid]="invalid('taxId', form.taxId, taxIdRequired())">
                  <label for="cu-tax">
                    {{ i18n.t('backoffice.customers.taxId') }}
                    <span class="bo-req">{{ i18n.t(taxIdRequired() ? 'common.required' : 'common.optional') }}</span>
                  </label>
                  <input
                    id="cu-tax" class="mono" dir="ltr" [(ngModel)]="form.taxId" [required]="taxIdRequired()" maxlength="40"
                    [attr.aria-invalid]="invalid('taxId', form.taxId, taxIdRequired()) ? 'true' : null"
                    [attr.aria-describedby]="invalid('taxId', form.taxId, taxIdRequired()) ? 'cu-tax-error' : null"
                    (blur)="touched.touch('taxId')"
                  />
                  @if (invalid('taxId', form.taxId, taxIdRequired())) {
                    <span id="cu-tax-error" class="bo-error" role="alert">{{ i18n.t('common.fieldRequired') }}</span>
                  }
                </div>
              } @else if (keepsPersonTaxId()) {
                <!-- A person saved with a number before the rule: it is shown, so it is not dropped
                     unseen, and it may stay exactly as it is or go — never become another number.
                     Text, not a control, because nothing here can be typed. -->
                <div class="bo-field bo-field--wide">
                  <p class="bo-fixed-label">{{ i18n.t('backoffice.customers.taxId') }}</p>
                  <p class="bo-fixed mono" id="cu-tax-kept"><span dir="ltr">{{ storedPersonTaxId() }}</span></p>
                  <p class="notice warn" id="cu-tax-person" role="status">{{ i18n.t('cust.taxId.personHasNone') }}</p>
                  <div class="bo-actions bo-actions--start">
                    <button type="button" class="btn-ghost bo-small" id="cu-tax-remove" (click)="removeTaxId()">
                      {{ i18n.t('cust.taxId.remove') }}
                    </button>
                  </div>
                </div>
              } @else if (taxIdEndsOnSave()) {
                <!-- A company or an undecided row made a person: the number goes in the same save
                     (the server refuses it otherwise), and the clerk hears it before saving. -->
                <div class="bo-field bo-field--wide">
                  <p class="bo-hint" id="cu-tax-ends">{{ i18n.t('cust.taxId.endsOnPerson') }}</p>
                </div>
              }
              <!-- A company's other identifiers — RC, AI, NIS in the Algeria preset — which its
                   invoice prints under the name (the owner's Facture N° 45, 2026-10-10). The labels
                   are the country profile's; a person has none. -->
              @if (kind() === 'Company') {
                @for (id of otherFiscalIds(); track id.key) {
                  <div class="bo-field">
                    <label [for]="'cu-fid-' + id.key">
                      {{ i18n.text(id.labels) || id.code }}
                      <span class="bo-req">{{ i18n.t('common.optional') }}</span>
                    </label>
                    <input
                      [id]="'cu-fid-' + id.key"
                      class="mono"
                      dir="ltr"
                      maxlength="40"
                      [ngModel]="fiscalIdValues[id.key] ?? ''"
                      (ngModelChange)="fiscalIdValues[id.key] = $event"
                    />
                  </div>
                }
              }
            </div>
            @if (showsIdDocument()) {
              <p class="bo-hint">{{ i18n.t('idDoc.contractHint') }}</p>
              <p class="bo-hint">{{ i18n.t('cust.rep.personHasNone') }}</p>
            }
          </app-fold>

          <!-- The copy of the identity document (owner, 2026-10-06): optional when the customer is
               added, and added later from the edit when it was not. Files chosen here go up with
               Save; one already kept is viewed or taken off at once. -->
          @if (showsScans()) {
            <div class="bo-group" id="cu-scans">
              <h3 class="bo-group__title">{{ i18n.t('cust.scan.title') }}</h3>
              <p class="bo-group__note">{{ i18n.t('cust.scan.hint') }}</p>
              @if (!canSeeScans()) {
                <!-- Kept copies are shown only with customers.documents.view (owner, 2026-10-25);
                     adding one at the counter needs no more than before. -->
                <p class="bo-soft" id="cu-scan-restricted">{{ i18n.t('cust.scan.restricted') }}</p>
              }
              @if (scans().length || pendingScans().length) {
                <ul class="scan-list">
                  @for (file of scans(); track file.id) {
                    <li class="scan-row">
                      <span class="scan-row__name">{{ file.fileName }}</span>
                      <span class="scan-row__meta">
                        <span class="bo-state bo-state--ok">{{ i18n.t('cust.scan.kept') }}</span>
                        <span class="mono">{{ sizeOf(file.length) }}</span>
                        <span class="mono">{{ day(file.uploadedAtUtc) }}</span>
                      </span>
                      <span class="scan-row__actions">
                        <button type="button" class="btn-ghost bo-small" [disabled]="scanBusy()" (click)="viewScan(file)">
                          {{ i18n.t('cust.scan.view') }}
                        </button>
                        <button type="button" class="btn-ghost bo-small" [disabled]="scanBusy()" (click)="removeScan(file)">
                          {{ i18n.t('cust.scan.remove') }}
                        </button>
                      </span>
                    </li>
                  }
                  @for (file of pendingScans(); track $index) {
                    <li class="scan-row scan-row--pending">
                      <span class="scan-row__name">{{ file.name }}</span>
                      <span class="scan-row__meta">
                        <span class="bo-state bo-state--warn">{{ i18n.t('cust.scan.pending') }}</span>
                        <span class="mono">{{ sizeOf(file.size) }}</span>
                      </span>
                      <span class="scan-row__actions">
                        <button type="button" class="btn-ghost bo-small" [disabled]="busy()" (click)="unqueueScan($index)">
                          {{ i18n.t('cust.scan.unqueue') }}
                        </button>
                      </span>
                    </li>
                  }
                </ul>
              } @else if (!scansLoading() && canSeeScans()) {
                <p class="bo-soft" id="cu-scan-none">{{ i18n.t('cust.scan.none') }}</p>
              }
              <div class="bo-field">
                <div class="bo-file">
                  <button
                    type="button" id="cu-scan-choose" class="btn-ghost"
                    [disabled]="busy() || scanBusy() || scanRoom() === 0"
                    (click)="cuScan.click()"
                  >
                    {{ i18n.t('cust.scan.add') }}
                  </button>
                  <input
                    #cuScan
                    id="cu-scan"
                    type="file"
                    hidden
                    multiple
                    accept="image/jpeg,image/png,image/webp,application/pdf"
                    (change)="chooseScans($event)"
                  />
                </div>
                <p class="bo-hint">{{ i18n.t('cust.scan.limits') }}</p>
                @if (scanError()) {
                  <span class="bo-error" role="alert" id="cu-scan-error">{{ scanError() }}</span>
                }
              </div>
            </div>
          }

          @if (showsRepresentative()) {
            <!-- Open: a company's representative's name is required, and a change is made here. -->
            <app-fold
              class="bo-group"
              [heading]="changing() ? i18n.t('cust.rep.changeTitle', { name: form.fullName }) : i18n.t('cust.rep.title')"
              [open]="true"
            >
              <p class="bo-group__note" foldLead>{{ i18n.t(changing() ? 'cust.rep.changeHint' : 'cust.rep.hint') }}</p>

              @if (endsOnSave() && !changing()) {
                <!-- The history is kept and simply stops being shown; coming back later is a
                     new appointment, not a resurrection, and the clerk hears it before saving. -->
                <p class="bo-hint">{{ i18n.t('cust.rep.endsOnPerson') }}</p>
              } @else if (changing() || needsRep()) {
                @if (!changing() && isEditingCompany()) {
                  <div class="bo-states">
                    <span class="bo-state bo-state--warn">{{ i18n.t('cust.rep.none') }}</span>
                  </div>
                }
                <div class="bo-fields">
                  <div class="bo-field" [class.is-invalid]="repNameMissing()">
                    <label for="cu-rep-name">
                      {{ i18n.t('cust.rep.fullName') }}<span class="bo-req">{{ i18n.t('common.required') }}</span>
                    </label>
                    <input
                      id="cu-rep-name" maxlength="200"
                      [ngModel]="repFullName()"
                      (ngModelChange)="repFullName.set($event)"
                      [attr.aria-invalid]="repNameMissing() ? 'true' : null"
                      [attr.aria-describedby]="repNameMissing() ? 'cu-rep-name-error' : null"
                      (blur)="touched.touch('repFullName')"
                    />
                    @if (repNameMissing()) {
                      <span id="cu-rep-name-error" class="bo-error" role="alert">{{ i18n.t('common.fieldRequired') }}</span>
                    }
                  </div>
                  <div class="bo-field">
                    <label for="cu-rep-id-type">
                      {{ i18n.t('idDoc.type') }}<span class="bo-req">{{ i18n.t('common.optional') }}</span>
                    </label>
                    <select id="cu-rep-id-type" [ngModel]="repIdType()" (ngModelChange)="repIdType.set($event)">
                      <option value="">{{ i18n.t('idDoc.type.none') }}</option>
                      @for (type of idTypes; track type) {
                        <option [value]="type">{{ i18n.t(idTypeKey(type)) }}</option>
                      }
                    </select>
                  </div>
                  <div class="bo-field" [class.is-invalid]="!!repIdProblems().idNumber">
                    <label for="cu-rep-id">
                      {{ i18n.t('cust.idDocument') }}<span class="bo-req">{{ i18n.t('common.optional') }}</span>
                    </label>
                    <input
                      id="cu-rep-id" class="mono" dir="ltr" maxlength="40"
                      [ngModel]="repIdDocument()"
                      (ngModelChange)="repIdDocument.set($event)"
                    />
                    @if (repIdProblems().idNumber; as key) {
                      <span class="bo-error" role="alert">{{ i18n.t(key) }}</span>
                    }
                  </div>
                  <div class="bo-field" [class.is-invalid]="!!repIdProblems().idIssuer">
                    <label for="cu-rep-id-issuer">
                      {{ i18n.t('idDoc.issuer') }}<span class="bo-req">{{ i18n.t('common.optional') }}</span>
                    </label>
                    <input
                      id="cu-rep-id-issuer" maxlength="120"
                      [ngModel]="repIdIssuer()"
                      (ngModelChange)="repIdIssuer.set($event)"
                    />
                    @if (repIdProblems().idIssuer; as key) {
                      <span class="bo-error" role="alert">{{ i18n.t(key) }}</span>
                    }
                  </div>
                  <div class="bo-field" [class.is-invalid]="!!repIdProblems().idIssuedOn">
                    <label for="cu-rep-id-issued">
                      {{ i18n.t('idDoc.issuedOn') }}<span class="bo-req">{{ i18n.t('common.optional') }}</span>
                    </label>
                    <app-date-input
                      id="cu-rep-id-issued"
                      [max]="todayDay()"
                      [ngModel]="repIdIssuedOn()"
                      (ngModelChange)="repIdIssuedOn.set($event ?? '')"
                    />
                    @if (repIdProblems().idIssuedOn; as key) {
                      <span class="bo-error" role="alert">{{ i18n.t(key) }}</span>
                    }
                  </div>
                  <div class="bo-field" [class.is-invalid]="phoneNotMobile(repPhone())">
                    <label for="cu-rep-phone">
                      {{ i18n.t('backoffice.customers.phone') }}<span class="bo-req">{{ i18n.t('common.optional') }}</span>
                    </label>
                    <input
                      id="cu-rep-phone" class="mono" dir="ltr" maxlength="60" inputmode="tel" autocomplete="tel"
                      [ngModel]="repPhone()"
                      (ngModelChange)="repPhone.set($event)"
                      [attr.aria-invalid]="phoneNotMobile(repPhone()) ? 'true' : null"
                      [attr.aria-describedby]="phoneNotMobile(repPhone()) ? 'cu-rep-phone-error' : null"
                    />
                    @if (phoneNotMobile(repPhone())) {
                      <span id="cu-rep-phone-error" class="bo-error" role="alert">{{ phoneHint() }}</span>
                    }
                  </div>
                  <div class="bo-field bo-field--wide">
                    <label for="cu-rep-note">
                      {{ i18n.t('cust.rep.note') }}<span class="bo-req">{{ i18n.t('common.optional') }}</span>
                    </label>
                    <input
                      id="cu-rep-note" maxlength="300"
                      [ngModel]="repNote()"
                      (ngModelChange)="repNote.set($event)"
                    />
                  </div>
                  @if (changing()) {
                    <!-- The window is the current one's first day to today. min/max guide the
                         picker; a day typed past them is still caught, said in the reader's
                         language, and keeps Confirm disabled — the server's English sentence
                         stays the fallback for API callers only. -->
                    <div class="bo-field" [class.is-invalid]="changeFromProblem()">
                      <label for="cu-rep-from">{{ i18n.t('cust.rep.from') }}</label>
                      <app-date-input
                        id="cu-rep-from"
                        [ngModel]="changeFrom()"
                        (ngModelChange)="changeFrom.set($event)"
                        [min]="changeMin()"
                        [max]="changeMax()"
                        [ariaInvalid]="changeFromProblem() ? 'true' : null"
                        [ariaDescribedby]="changeFromProblem() ? 'cu-rep-from-error' : 'cu-rep-from-hint'"
                      />
                      <p class="bo-hint" id="cu-rep-from-hint">{{ i18n.t('cust.rep.fromHint') }}</p>
                      @if (changeFromProblem(); as problem) {
                        <span id="cu-rep-from-error" class="bo-error" role="alert">
                          {{ i18n.t(problem === 'fromTooEarly' ? 'cust.rep.fromTooEarly' : 'cust.rep.fromInFuture', { day: day(current()?.fromUtc ?? null) }) }}
                        </span>
                      }
                    </div>
                  }
                </div>
                @if (changing()) {
                  <div class="bo-actions">
                    <button type="button" class="btn-ghost" (click)="cancelChange()">{{ i18n.t('common.cancel') }}</button>
                    <button
                      type="button"
                      class="btn-primary"
                      [disabled]="busy() || !repFullName().trim() || changeFromProblem() !== null"
                      (click)="confirmChange()"
                    >
                      {{ i18n.t('cust.rep.confirm') }}
                    </button>
                  </div>
                }
              } @else {
                @if (current(); as rep) {
                  <p class="bo-meta">
                    <span class="bo-state bo-state--lead">{{ i18n.t('cust.rep.current') }}</span>
                    <span>{{ rep.fullName }}</span>
                    @if (rep.idDocumentType) {
                      <span>{{ i18n.t(idTypeKey(rep.idDocumentType)) }}</span>
                    }
                    @if (rep.idDocumentNumber) {
                      <span class="mono">{{ rep.idDocumentNumber }}</span>
                    }
                    @if (rep.phone) {
                      <span class="mono" dir="ltr">{{ phoneShown(rep.phone) }}</span>
                    }
                    <span>{{ i18n.t('cust.rep.since', { day: day(rep.fromUtc) }) }}</span>
                  </p>
                  <div class="bo-actions bo-actions--start">
                    <button type="button" class="btn-ghost bo-small" (click)="openChange()">{{ i18n.t('cust.rep.change') }}</button>
                  </div>
                }
              }

              @if (isEditingCompany() && (historyLoading() || history().length)) {
                <h4 class="bo-group__title">{{ i18n.t('cust.rep.history') }}</h4>
                @if (historyLoading()) {
                  <span class="bo-soft">{{ i18n.t('common.loading') }}</span>
                } @else {
                  <div class="bo-scroll">
                    <table class="bo-data">
                      <thead>
                        <tr>
                          <th>{{ i18n.t('cust.rep.fullName') }}</th>
                          <th>{{ i18n.t('cust.idDocument') }}</th>
                          <th>{{ i18n.t('backoffice.customers.phone') }}</th>
                          <th>{{ i18n.t('cust.rep.from') }}</th>
                          <th>{{ i18n.t('cust.rep.to') }}</th>
                          <th>{{ i18n.t('cust.rep.note') }}</th>
                          <th>{{ i18n.t('cust.rep.recordedBy') }}</th>
                        </tr>
                      </thead>
                      <tbody>
                        @for (r of history(); track r.id) {
                          <tr>
                            <td [attr.data-label]="i18n.t('cust.rep.fullName')">{{ r.fullName }}</td>
                            <td [attr.data-label]="i18n.t('cust.idDocument')">
                              <span class="mono">{{ r.idDocumentNumber || '—' }}</span>
                              @if (r.idDocumentType || r.idDocumentIssuer || r.idDocumentIssuedOn) {
                                <!-- Which paper, who issued it and when: the rest of what a contract signed by
                                     this person recorded, kept on the row they signed under. -->
                                <div class="bo-soft id-paper">
                                  @if (r.idDocumentType) {
                                    <span>{{ i18n.t(idTypeKey(r.idDocumentType)) }}</span>
                                  }
                                  @if (r.idDocumentIssuer) {
                                    <span>{{ r.idDocumentIssuer }}</span>
                                  }
                                  @if (r.idDocumentIssuedOn) {
                                    <span class="mono">{{ issuedDay(r.idDocumentIssuedOn) }}</span>
                                  }
                                </div>
                              }
                            </td>
                            <td class="mono" [attr.data-label]="i18n.t('backoffice.customers.phone')">{{ r.phone || '—' }}</td>
                            <td [attr.data-label]="i18n.t('cust.rep.from')">{{ day(r.fromUtc) }}</td>
                            <td [attr.data-label]="i18n.t('cust.rep.to')">
                              @if (r.toUtc) {
                                {{ day(r.toUtc) }}
                              } @else {
                                <span class="bo-state bo-state--lead">{{ i18n.t('cust.rep.current') }}</span>
                              }
                            </td>
                            <td [attr.data-label]="i18n.t('cust.rep.note')">{{ r.note || '—' }}</td>
                            <td [attr.data-label]="i18n.t('cust.rep.recordedBy')">{{ r.createdBy || '—' }}</td>
                          </tr>
                        }
                      </tbody>
                    </table>
                  </div>
                  @if (history().length === 1) {
                    <span class="bo-soft">{{ i18n.t('cust.rep.noHistory') }}</span>
                  }
                }
              }
            </app-fold>
          }

          <!-- Open: the phone is what a customer is reached by, and the company may require any of
               these (Store settings). -->
          <app-fold class="bo-group" [heading]="i18n.t('common.group.contact')" [open]="true">
            <div class="bo-fields">
              <div class="bo-field" [class.is-invalid]="invalid('phone', form.phone, req.phone) || phoneNotMobile(form.phone, editing()?.phone)">
                <label for="cu-phone">
                  {{ i18n.t('backoffice.customers.phone') }}
                  <span class="bo-req">{{ i18n.t(req.phone ? 'common.required' : 'common.optional') }}</span>
                </label>
                <input
                  id="cu-phone" class="mono" dir="ltr" inputmode="tel" autocomplete="tel" [(ngModel)]="form.phone" [required]="req.phone"
                  [attr.aria-invalid]="invalid('phone', form.phone, req.phone) || phoneNotMobile(form.phone, editing()?.phone) ? 'true' : null"
                  [attr.aria-describedby]="invalid('phone', form.phone, req.phone) || phoneNotMobile(form.phone, editing()?.phone) ? 'cu-phone-error' : null"
                  (blur)="touched.touch('phone')"
                />
                @if (invalid('phone', form.phone, req.phone)) {
                  <span id="cu-phone-error" class="bo-error" role="alert">{{ i18n.t('common.fieldRequired') }}</span>
                } @else if (phoneNotMobile(form.phone, editing()?.phone)) {
                  <span id="cu-phone-error" class="bo-error" role="alert">{{ phoneHint() }}</span>
                }
              </div>
              <div class="bo-field" [class.is-invalid]="invalid('email', form.email, req.email)">
                <label for="cu-email">
                  {{ i18n.t('backoffice.customers.email') }}
                  <span class="bo-req">{{ i18n.t(req.email ? 'common.required' : 'common.optional') }}</span>
                </label>
                <input
                  id="cu-email" type="email" [(ngModel)]="form.email" [required]="req.email"
                  [attr.aria-invalid]="invalid('email', form.email, req.email) ? 'true' : null"
                  [attr.aria-describedby]="invalid('email', form.email, req.email) ? 'cu-email-error' : null"
                  (blur)="touched.touch('email')"
                />
                @if (invalid('email', form.email, req.email)) {
                  <span id="cu-email-error" class="bo-error" role="alert">{{ i18n.t('common.fieldRequired') }}</span>
                }
              </div>
              <div class="bo-field" [class.is-invalid]="invalid('birthDate', form.birthDate, req.birthDate) || !!ownIdProblems().birthDate">
                <label for="cu-birth">
                  {{ i18n.t('backoffice.customers.birthDate') }}
                  <span class="bo-req">{{ i18n.t(req.birthDate ? 'common.required' : 'common.optional') }}</span>
                </label>
                <app-date-input
                  id="cu-birth"
                  [(ngModel)]="form.birthDate"
                  [required]="req.birthDate"
                  [ariaInvalid]="invalid('birthDate', form.birthDate, req.birthDate) ? 'true' : null"
                  [ariaDescribedby]="invalid('birthDate', form.birthDate, req.birthDate) ? 'cu-birth-error' : null"
                  (blur)="touched.touch('birthDate')"
                />
                @if (invalid('birthDate', form.birthDate, req.birthDate)) {
                  <span id="cu-birth-error" class="bo-error" role="alert">{{ i18n.t('common.fieldRequired') }}</span>
                } @else if (ownIdProblems().birthDate) {
                  <span class="bo-error" role="alert">{{ i18n.t(ownIdProblems().birthDate!) }}</span>
                }
              </div>
              <div class="bo-field bo-field--wide" [class.is-invalid]="invalid('address', form.address, req.address)">
                <label for="cu-address">
                  {{ i18n.t('backoffice.customers.address') }}
                  <span class="bo-req">{{ i18n.t(req.address ? 'common.required' : 'common.optional') }}</span>
                </label>
                <input
                  id="cu-address" [(ngModel)]="form.address" [required]="req.address"
                  [attr.aria-invalid]="invalid('address', form.address, req.address) ? 'true' : null"
                  [attr.aria-describedby]="invalid('address', form.address, req.address) ? 'cu-address-error' : null"
                  (blur)="touched.touch('address')"
                />
                @if (invalid('address', form.address, req.address)) {
                  <span id="cu-address-error" class="bo-error" role="alert">{{ i18n.t('common.fieldRequired') }}</span>
                }
              </div>
            </div>
          </app-fold>

          <app-fold class="bo-group" [heading]="i18n.t('backoffice.customers.creditLimit')">
            <div class="bo-fields">
              <div class="bo-field">
                <label for="cu-limit">{{ i18n.t('backoffice.customers.creditLimit') }}</label>
                <input id="cu-limit" type="number" min="0" step="1" class="num mono" [(ngModel)]="form.creditLimit" />
              </div>
            </div>
          </app-fold>

          <!-- The balance the customer starts with on the system (owner, 2026-10-10, go-live): typed
               while registering, or set once later by whoever holds customers.opening_balance, then
               only corrected with a reason. Not on the quick-create a till or a contract opens. -->
          @if (!createOnly()) {
            <app-fold class="bo-group" [heading]="i18n.t('cust.opening.title')" [open]="openingInvalid()">
              <app-customer-opening-balance
                [party]="editing()"
                [(draft)]="openingDraft"
                (changed)="openingChanged($event)"
              />
            </app-fold>
          }
          }
        </div>
        <div class="bo-panel__foot">
          <button type="button" class="btn-ghost" (click)="cancelEdit()">{{ i18n.t('common.cancel') }}</button>
          @if (!choosingKind()) {
            <button type="button" class="btn-primary" [disabled]="busy()" (click)="save()">
              {{ i18n.t('common.save') }}
            </button>
          }
        </div>
      </section>

      @if (!createOnly()) {
      <!-- The list page's own chrome (Paradis, Batch 2): "New" in the page header, the search in the
           page toolbar. Never in create-only mode, where this form sits inside another page. -->
      <ng-template appPageActions>
        <button type="button" class="btn-primary" (click)="openCreate()">
          <span aria-hidden="true">+</span> {{ i18n.t('backoffice.customers.new') }}
        </button>
      </ng-template>
      <ng-template appPageToolbar>
        <div class="ui-toolbar" role="search">
          <div class="ui-filter ui-filter--search">
            <label for="cu-q">{{ i18n.t('backoffice.customers.search') }}</label>
            <input
              id="cu-q"
              type="search"
              class="finder"
              [placeholder]="i18n.t('backoffice.customers.search')"
              [ngModel]="query()"
              (ngModelChange)="query.set($event)"
            />
          </div>
          <div class="ui-filter">
            <label for="cu-f-kind">{{ i18n.t('cust.filter.kind') }}</label>
            <select id="cu-f-kind" [ngModel]="kindFilter()" (ngModelChange)="kindFilter.set($event)">
              <option ngValue="all">{{ i18n.t('filters.all') }}</option>
              <option ngValue="Person">{{ i18n.t('cust.kind.person') }}</option>
              <option ngValue="Company">{{ i18n.t('cust.kind.company') }}</option>
              <option ngValue="Unspecified">{{ i18n.t('cust.kind.unset') }}</option>
            </select>
          </div>
          <div class="ui-filter">
            <label for="cu-f-balance">{{ i18n.t('cust.filter.balance') }}</label>
            <select id="cu-f-balance" [ngModel]="balanceFilter()" (ngModelChange)="balanceFilter.set($event)">
              <option ngValue="all">{{ i18n.t('filters.all') }}</option>
              <option ngValue="owes">{{ i18n.t('cust.filter.owes') }}</option>
              <option ngValue="overLimit">{{ i18n.t('cust.filter.overLimit') }}</option>
              <option ngValue="inCredit">{{ i18n.t('cust.filter.inCredit') }}</option>
              <option ngValue="settled">{{ i18n.t('cust.filter.settled') }}</option>
            </select>
          </div>
          <div class="ui-filter">
            <label for="cu-f-points">{{ i18n.t('cust.filter.points') }}</label>
            <select id="cu-f-points" [ngModel]="pointsFilter()" (ngModelChange)="pointsFilter.set($event)">
              <option ngValue="all">{{ i18n.t('filters.all') }}</option>
              <option ngValue="has">{{ i18n.t('cust.filter.hasPoints') }}</option>
              <option ngValue="none">{{ i18n.t('cust.filter.noPoints') }}</option>
            </select>
          </div>
          <div class="ui-filter">
            <label for="cu-f-missing">{{ i18n.t('cust.filter.missing') }}</label>
            <select id="cu-f-missing" [ngModel]="missingFilter()" (ngModelChange)="missingFilter.set($event)">
              <option ngValue="all">{{ i18n.t('filters.all') }}</option>
              <option ngValue="phone">{{ i18n.t('cust.filter.noPhone') }}</option>
              <option ngValue="email">{{ i18n.t('cust.filter.noEmail') }}</option>
              <option ngValue="representative">{{ i18n.t('cust.filter.noRep') }}</option>
              <option ngValue="phoneToFix">{{ i18n.t('cust.filter.phoneToFix') }}</option>
            </select>
          </div>
          @if (filtered()) {
            <div class="ui-toolbar__end">
              <button type="button" class="btn-ghost" (click)="clearFilters()">{{ i18n.t('ui.state.clearFilters') }}</button>
            </div>
          }
        </div>
      </ng-template>
      <section class="ui-surface">
        <div class="bo-scroll bo-scroll--tall">
          <table class="bo-data">
            <thead>
              <tr>
                <th class="col-code">{{ i18n.t('backoffice.customers.code') }}</th>
                <th class="col-name">{{ i18n.t('backoffice.customers.fullName') }}</th>
                <th class="col-phone">{{ i18n.t('backoffice.customers.phone') }}</th>
                <th class="col-email">{{ i18n.t('backoffice.customers.email') }}</th>
                <th class="col-limit num">{{ i18n.t('backoffice.customers.creditLimit') }}</th>
                <th class="col-balance num">{{ i18n.t('backoffice.customers.balance') }}</th>
                <th class="col-points num">{{ i18n.t('backoffice.customers.points') }}</th>
                <th class="col-actions"><span class="sr-only">{{ i18n.t('common.actions') }}</span></th>
              </tr>
            </thead>
            <tbody>
              @for (c of pager.rows(); track c.id) {
                <tr>
                  <!-- Its own left-to-right run (core/bidi.ts), so an Arabic row keeps its direction around it. -->
                  <td class="col-code mono" [attr.data-label]="i18n.t('backoffice.customers.code')"><span dir="ltr">{{ c.code }}</span></td>
                  <td class="col-name" [attr.data-label]="i18n.t('backoffice.customers.fullName')">
                    <!-- The record's name opens it (the list pattern); the other actions are in the row menu. -->
                    <button type="button" class="ui-link" (click)="edit(c)">{{ c.fullName }}</button>
                    <!-- The kind and, for a company, who signs — as a second line rather than a
                         ninth column. In words beside the colour: the warn chip says what it is. -->
                    <div class="kind-line bo-states">
                      @if (c.kind === 'Unspecified') {
                        <span class="bo-state bo-state--warn">{{ i18n.t('cust.kind.unset') }}</span>
                      } @else {
                        <span class="bo-soft">{{ i18n.t(kindLabelKey(c.kind)) }}</span>
                      }
                      @if (c.kind === 'Company') {
                        @if (c.representative) {
                          <span class="bo-soft">{{ i18n.t('cust.rep.label', { name: c.representative.fullName }) }}</span>
                        } @else {
                          <span class="bo-state bo-state--warn">{{ i18n.t('cust.rep.none') }}</span>
                        }
                      }
                    </div>
                  </td>
                  <td class="col-phone mono" [attr.data-label]="i18n.t('backoffice.customers.phone')"><span dir="ltr">{{ c.phone ? phoneShown(c.phone) : '—' }}</span></td>
                  <td class="col-email" [attr.data-label]="i18n.t('backoffice.customers.email')">{{ c.email || '—' }}</td>
                  <td class="col-limit num mono" [attr.data-label]="i18n.t('backoffice.customers.creditLimit')">
                    {{ i18n.money(c.creditLimit) }}
                  </td>
                  <td class="col-balance num mono bo-cell-strong" [attr.data-label]="i18n.t('backoffice.customers.balance')">
                    <span class="col-balance__amount">{{ i18n.money(c.balance) }}</span>
                    @if (overLimit(c)) {
                      <div class="col-balance__badge">
                        <span class="bo-state bo-state--danger">{{ i18n.t('backoffice.customers.overLimit') }}</span>
                      </div>
                    }
                  </td>
                  <td class="col-points num mono" [attr.data-label]="i18n.t('backoffice.customers.points')">{{ c.loyaltyPoints }}</td>
                  <td class="col-actions ui-cell-menu">
                    <app-menu triggerClass="btn-ghost ui-more" [label]="i18n.t('ui.menu.more') + ' — ' + c.fullName">
                      <span menuTrigger aria-hidden="true">⋯</span>
                      <button type="button" appMenuItem (click)="openAccount.emit(c.id)">{{ i18n.t('inv.openAccount') }}</button>
                      <button type="button" appMenuItem (click)="edit(c)">{{ i18n.t('common.edit') }}</button>
                      @if (c.kind === 'Company' && c.representative) {
                        <button type="button" appMenuItem (click)="changeFromRow(c)">{{ i18n.t('cust.rep.change') }}</button>
                      }
                      <hr appMenuSeparator />
                      <button type="button" appMenuItem class="is-danger" (click)="remove(c)">{{ i18n.t('common.delete') }}</button>
                    </app-menu>
                  </td>
                </tr>
              } @empty {
                <tr>
                  <td colspan="8" class="bo-empty">
                    @if (!pager.loaded()) {
                      <strong>{{ i18n.t('common.loading') }}</strong>
                      <span class="bo-loading__rows" aria-hidden="true"></span>
                    } @else if (filtered()) {
                      <app-table-state kind="noResults" (clear)="clearFilters()" />
                    } @else {
                      <strong>{{ i18n.t('backoffice.customers.none') }}</strong>
                      @if (query()) {
                        {{ i18n.t('reports.emptyHint') }}
                      }
                    }
                  </td>
                </tr>
              }
            </tbody>
          </table>
        </div>
      </section>
      <app-list-footer [pager]="pager" [loading]="!pager.loaded()" />
      }
    </div>
  `,
  styleUrls: ['./backoffice-ui.scss', '../../ui/ui-patterns.scss'],
  styles: [
    `
      .finder {
        min-inline-size: min(18rem, 100%);
        min-block-size: var(--control-h-sm);
        padding: 0 12px;
        font-size: var(--fs-sm);
        border: 1px solid var(--border);
        border-radius: var(--radius-sm);
        background: var(--surface);
      }
      /* "Generated on save" and the kind's pattern as two runs with a gap rather than a typed
         separator, which would land on the wrong side of a Latin pattern in Arabic. */
      .code-new {
        display: flex;
        flex-wrap: wrap;
        gap: 0 0.6rem;
      }
      /* The new customer's dialog while it asks the kind: the shared dialog's small size. Its
         growth into the editor is animated by grow(), over --t-slide, and not at all under
         prefers-reduced-motion. Centred from both edges, so it grows the same way in Arabic. */
      .bo-panel.bo-edit-modal.is-choosing {
        inline-size: min(32rem, calc(100vw - 2rem));
      }
      /* The first question of a new customer: two large choices, side by side when there is room.
         Nothing is chosen while they show, so they differ from each other by words, not state;
         hover fills them (tint), it does not only edge them. */
      .kind-pick {
        display: grid;
        gap: var(--sp-3);
      }
      .kind-pick .bo-group__title {
        margin: 0;
      }
      .kind-pick__choices {
        display: grid;
        grid-template-columns: repeat(auto-fit, minmax(min(12rem, 100%), 1fr));
        gap: var(--sp-4);
      }
      .kind-pick__choice {
        display: grid;
        align-content: start;
        gap: var(--sp-2);
        min-block-size: 7rem;
        padding: var(--sp-4);
        border: 1px solid var(--border);
        border-radius: var(--radius-card);
        background: var(--surface);
        color: var(--text);
        text-align: start;
        cursor: pointer;
        transition: background 0.12s ease, border-color 0.12s ease;
      }
      .kind-pick__choice:hover {
        background: var(--surface-tint);
        border-color: var(--accent);
      }
      .kind-pick__name {
        font-size: var(--fs-lg);
        font-weight: 600;
      }
      .kind-pick__hint {
        font-size: var(--fs-sm);
        color: var(--muted);
      }
      .kind-line {
        margin-block-start: 0.15rem;
        font-size: 0.86rem;
      }
      /* Each part of the paper is its own run, spaced rather than joined by a typed separator,
         which would land on the wrong side between an Arabic issuer and a Latin date. */
      .id-paper {
        display: flex;
        flex-wrap: wrap;
        gap: 0 0.6rem;
        font-size: 0.86rem;
      }
      /* The kept and the chosen copies, one row each: the name, then its state in words, size and
         day as separate runs (no typed separator to land on the wrong side in Arabic), then its
         actions. Rows wrap on a phone rather than scroll. */
      .scan-list {
        display: grid;
        gap: var(--sp-2);
        margin: 0 0 var(--sp-3);
        padding: 0;
        list-style: none;
      }
      .scan-row {
        display: flex;
        flex-wrap: wrap;
        align-items: center;
        gap: var(--sp-2) var(--sp-3);
        padding: var(--sp-2) var(--sp-3);
        border: 1px solid var(--border);
        border-radius: var(--radius-sm);
        background: var(--surface);
      }
      .scan-row--pending {
        background: var(--surface-2);
      }
      .scan-row__name {
        flex: 1 1 12rem;
        min-inline-size: 0;
        overflow-wrap: anywhere;
      }
      .scan-row__meta {
        display: flex;
        flex-wrap: wrap;
        align-items: center;
        gap: 0 var(--sp-3);
        font-size: var(--fs-sm);
        color: var(--muted);
      }
      .scan-row__actions {
        display: flex;
        gap: var(--sp-2);
        margin-inline-start: auto;
      }
      .col-code {
        white-space: nowrap;
        inline-size: 1%;
      }
      .col-phone {
        white-space: nowrap;
        inline-size: 1%;
      }
      .col-email {
        white-space: nowrap;
        max-inline-size: 11rem;
        text-overflow: ellipsis;
        overflow: hidden;
        font-size: 12.5px;
      }
      .col-limit {
        white-space: normal;
        inline-size: 1%;
        line-height: 1.25;
      }
      .col-balance {
        white-space: nowrap;
        inline-size: 1%;
        text-align: end;

        .col-balance__amount {
          display: block;
        }

        .col-balance__badge {
          margin-block-start: 2px;
          display: flex;
          justify-content: flex-end;
        }
      }
      .col-points {
        white-space: nowrap;
        inline-size: 1%;
      }
      .col-name {
        min-inline-size: 9rem;
      }
      .col-actions {
        white-space: normal;
        text-align: end;
        inline-size: 1%;
      }
    `,
  ],
})
export class BackofficeCustomersComponent implements OnInit {
  private readonly admin = inject(AdminService);
  private readonly confirm = inject(ConfirmService);
  private readonly regional = inject(RegionalService);
  private readonly branchScope = inject(BranchScopeService);
  private readonly auth = inject(AuthService);
  private readonly injector = inject(Injector);
  /** The editor's own dialog: it starts small for a new customer and grows (grow()). */
  private readonly editorPanel = viewChild<ElementRef<HTMLElement>>('editor');
  readonly i18n = inject(I18nService);

  /** The customer whose account — statement, invoices, receipts — the reader wants to open. */
  readonly openAccount = output<string>();

  /**
   * FR-1 (the owner, 2026-10-03): the same editor, opened from the rental form or the till to
   * create one customer without leaving them. Only the new-customer editor is drawn — no list —
   * and it reports back instead of reloading: `created` with the customer the server returned, or
   * `closed` when the reader cancelled. Fields, required-field settings, checks and the API call
   * are this editor's own, unchanged.
   */
  readonly createOnly = input(false);
  readonly created = output<AdminCustomer>();
  readonly closed = output<void>();
  /**
   * The branch the host is working at, when it has one (owner, 2026-10-05): a customer created
   * from a rental contract takes the contract's branch, whose code goes into theirs. Null — the
   * customers page, the till, a contract whose branch is not chosen yet — falls back to the
   * working-branch rule. The server still gives anyone locked to a branch their own.
   */
  readonly atBranch = input<string | null>(null);

  /** The page on screen; the server searches, filters and pages (owner, 2026-11-17). */
  readonly customers = computed(() => this.pager.rows());
  readonly busy = signal(false);
  readonly error = signal<string | null>(null);
  /** What just went right — a representative change is not a save, so it says so. */
  readonly notice = signal<string | null>(null);
  /** The row being edited, whole: the kind rules and the representative block read more than its id. */
  readonly editing = signal<AdminCustomer | null>(null);
  /** The opening balance typed while registering; sent with the create, never with an edit. */
  readonly openingDraft = signal(emptyOpeningDraft(localToday()));
  /** A field marked invalid opens its section (owner, 2026-10-08). */
  readonly openingInvalid = computed(() => openingDraftProblem(this.openingDraft(), localToday()) !== null);
  readonly editingId = computed(() => this.editing()?.id ?? null);
  readonly editorOpen = signal(false);
  /** Without a filter a customer file is a scroll, not a list. */
  readonly query = signal('');

  // The kind and the representative live beside `form`, not in it: they decide which of the
  // form's fields show, and the representative is a row of its own on the server.
  readonly kind = signal<CustomerKind>('Unspecified');
  readonly idDocumentNumber = signal('');
  // The rest of the paper (owner, 2026-09-30): optional here, required by the rental contract,
  // which prefills its signatory from these — so they are sent exactly as typed, trimmed.
  readonly idDocumentType = signal('');
  readonly idDocumentIssuer = signal('');
  readonly idDocumentIssuedOn = signal('');
  readonly repIdType = signal('');
  readonly repIdIssuer = signal('');
  readonly repIdIssuedOn = signal('');
  readonly idTypes = ID_DOCUMENT_TYPES;
  readonly idTypeKey = idDocumentTypeKey;
  readonly repFullName = signal('');
  readonly repIdDocument = signal('');
  readonly repPhone = signal('');
  readonly repNote = signal('');
  /** The change block is open: the representative left and someone else signs from a given day. */
  readonly changing = signal(false);
  readonly changeFrom = signal('');
  /** Today in the company's zone, fixed when the change block opens: the latest day a takeover may be dated. */
  readonly changeMax = signal('');
  readonly history = signal<Representative[]>([]);
  readonly historyLoading = signal(false);

  /** The copies of the identity document already kept with the row being edited. */
  readonly scans = signal<CustomerAttachment[]>([]);
  readonly scansLoading = signal(false);
  /** Files chosen in this editor, sent with Save — a new customer has no id to send them to before. */
  readonly pendingScans = signal<File[]>([]);
  readonly scanBusy = signal(false);
  readonly scanError = signal<string | null>(null);
  /** How many more files the customer may keep: the server keeps ten at most. */
  readonly scanRoom = computed(() => Math.max(0, MAX_SCANS - this.scans().length - this.pendingScans().length));

  readonly needsRep = computed(() => needsRepresentative(this.kind(), this.editing()));
  readonly endsOnSave = computed(() => representativeEndsOnSave(this.kind(), this.editing()));
  readonly showsRepresentative = computed(() => this.kind() === 'Company' || this.endsOnSave());
  readonly isEditingCompany = computed(() => this.editing()?.kind === 'Company');
  /** A migrated row that has not said what it is yet. Never true on a new customer, which simply has not chosen. */
  readonly kindUnset = computed(() => this.editing() !== null && this.kind() === 'Unspecified');
  /**
   * A new customer that has not said what it is (owner, 2026-10-05): the editor shows the two
   * choices and nothing else, and Save stays disabled. There is no default kind.
   */
  readonly choosingKind = computed(() => this.editing() === null && this.kind() === 'Unspecified');
  /** A person's own document, or a legacy row's while it is still undecided; a company never has one. */
  readonly showsIdDocument = computed(() => this.kind() === 'Person' || this.kindUnset());
  /** A person's copy, or one kept before the row became a company, so it is never on file unseen. */
  readonly showsScans = computed(() => this.showsIdDocument() || this.scans().length > 0);

  /**
   * Whether the copies already kept may be listed and opened: customers.documents.view, read from
   * the user's capabilities, never a role (owner, 2026-10-25). Without it the server refuses them.
   */
  readonly canSeeScans = computed(() => {
    const caps = this.auth.currentUser()?.capabilities;
    return Array.isArray(caps) && caps.includes('customers.documents.view');
  });
  /**
   * The tax number is a company's (owner, 2026-10-05: "الرقم الجبائي للشركات فقط"): the field is
   * typed into for a company, and for a legacy row while nobody has said what it is — as before.
   * A person has none, so neither a new one nor a changed one is offered.
   */
  readonly showsTaxId = computed(() => this.kind() === 'Company' || this.kindUnset());
  /**
   * The shape of the code a new customer of the chosen kind will be given — words and a pattern,
   * never a number: the server takes the next one only when Save is pressed. Null until a kind is chosen.
   */
  readonly codePatternKey = computed(() => {
    const kind = this.kind();
    return kind === 'Person' ? 'cust.code.pattern.person' : kind === 'Company' ? 'cust.code.pattern.company' : null;
  });
  /** The number a row that was already a person carries from before the rule; null when it has none. */
  readonly storedPersonTaxId = computed(() => {
    const row = this.editing();
    return row?.kind === 'Person' ? row.taxId?.trim() || null : null;
  });
  /** Remove was pressed on that number: the save sends it blank, which is how it goes. */
  readonly taxIdRemoved = signal(false);
  /** Still a person, still carrying it: shown as it is, with Remove beside it — never retyped. */
  readonly keepsPersonTaxId = computed(
    () => this.kind() === 'Person' && this.storedPersonTaxId() !== null && !this.taxIdRemoved(),
  );
  /**
   * A company or an undecided row with a number, made a person in this save: the number goes with
   * it. The server allows a person to keep only the number they were already saved with as one.
   */
  readonly taxIdEndsOnSave = computed(() => {
    const row = this.editing();
    return this.kind() === 'Person' && row !== null && row.kind !== 'Person' && !!row.taxId?.trim();
  });
  readonly current = computed(() => this.editing()?.representative ?? null);
  /** The earliest day a takeover may be dated: the day the current representative started, in the company's zone. */
  readonly changeMin = computed(() => {
    const from = this.current()?.fromUtc;
    return from ? localDay(from, this.regional.settings().timeZone) : undefined;
  });
  /** Said on screen, in words, before the server would say it in English. */
  readonly changeFromProblem = computed(() => takeoverDayProblem(this.changeFrom(), this.changeMin(), this.changeMax()));

  // The list's filters (owner, 2026-11-17). Each narrows the rows already loaded; none is a request.
  readonly kindFilter = signal<'all' | CustomerKind>('all');
  readonly balanceFilter = signal<'all' | 'owes' | 'overLimit' | 'inCredit' | 'settled'>('all');
  readonly pointsFilter = signal<'all' | 'has' | 'none'>('all');
  readonly missingFilter = signal<'all' | 'phone' | 'email' | 'representative' | 'phoneToFix'>('all');

  readonly filtered = computed(
    () =>
      !!this.query().trim() ||
      this.kindFilter() !== 'all' ||
      this.balanceFilter() !== 'all' ||
      this.pointsFilter() !== 'all' ||
      this.missingFilter() !== 'all',
  );

  /**
   * The customers list a page at a time from the server (owner, 2026-11-17: "paginate in backend,
   * not frontend"): the search and every filter run there, over all customers; a change goes back
   * to page one. Not loaded in create-only mode, where this form sits inside another page.
   */
  readonly pager = createServerPager(
    (page) => this.admin.customersPage({ skip: page.skip, take: page.take, ...page.filters }),
    {
      key: 'customers',
      filters: () => ({
        q: this.query().trim(),
        kind: this.kindFilter(),
        balance: this.balanceFilter(),
        points: this.pointsFilter(),
        missing: this.missingFilter(),
      }),
      enabled: () => !this.createOnly(),
    },
  );

  /** The list could not be read, in words. */
  readonly listError = computed(() => {
    const e = this.pager.error();
    return e ? adminErrorMessage(e) : null;
  });

  clearFilters(): void {
    this.query.set('');
    this.kindFilter.set('all');
    this.balanceFilter.set('all');
    this.pointsFilter.set('all');
    this.missingFilter.set('all');
  }

  /** Which required fields have earned the right to complain. */
  readonly touched = new TouchedFields();

  /**
   * Whether a field should be saying it is empty. Which fields are mandatory here is a company
   * setting rather than a constant, so the flag is passed in: a blank tax number is a refusal
   * in one company and perfectly ordinary in the next.
   */
  invalid(name: string, value: unknown, required: boolean): boolean {
    return required && this.touched.missing(name, value);
  }

  /** Which fields the admin has marked mandatory, read from Store settings. */
  req = { phone: false, email: false, birthDate: false, address: false, taxId: false };
  readonly required = signal(this.req);
  /** The tax number counts only where it is asked: of a company. */
  readonly anyRequired = computed(() => {
    const r = this.required();
    return r.phone || r.email || r.birthDate || r.address || (r.taxId && this.kind() === 'Company');
  });

  /**
   * Whether the tax number must be filled in: the store asks it of a company only (owner,
   * 2026-10-05). A person has none, and a row whose kind is not set yet may well be a person.
   */
  taxIdRequired(): boolean {
    return this.req.taxId && this.kind() === 'Company';
  }

  form: CustomerFields = this.emptyForm();
  /** A company's other fiscal identifiers by profile key, as typed. */
  fiscalIdValues: Record<string, string> = {};
  /** The identifiers asked beside the tax number, in the profile's order. */
  readonly otherFiscalIds = computed(() => customerOtherFiscalIds(this.regional.settings()));

  readonly kindLabelKey = kindLabelKey;

  async ngOnInit(): Promise<void> {
    // The list loads itself through its pager; create-only mode never reads it.
    if (this.createOnly()) this.openCreate();
    try {
      const store: AdminStore = await this.admin.getStore();
      this.req = {
        phone: store.requireCustomerPhone,
        email: store.requireCustomerEmail,
        birthDate: store.requireCustomerBirthDate,
        address: store.requireCustomerAddress,
        taxId: store.requireCustomerTaxId,
      };
      this.required.set(this.req);
    } catch {
      /* store settings unavailable — fall back to nothing mandatory */
    }
  }

  private emptyForm(): CustomerFields {
    return {
      fullName: '',
      firstName: '',
      familyName: '',
      phone: null,
      address: null,
      taxId: null,
      email: null,
      birthDate: null,
      creditLimit: 0,
    };
  }

  /** A credit limit that has been passed is a warning, never a block — the sale already happened. */
  overLimit(c: AdminCustomer): boolean {
    return c.creditLimit > 0 && c.balance > c.creditLimit;
  }

  /**
   * The phone typed is not a mobile of the company's country (owner, 2026-11-17; core/phone.ts),
   * so the server would refuse it. One sent back exactly as it is stored — a number from before
   * the rule — goes through: it is fixed by hand, never a reason to refuse another change.
   */
  phoneNotMobile(value: string | null | undefined, stored?: string | null): boolean {
    const typed = (value ?? '').trim();
    if (!typed) return false;
    const rules = this.regional.settings().phone;
    if (stored != null && (typed === stored.trim() || typed === formatPhone(stored, rules))) return false;
    return !normalizePhone(typed, rules).isMobile;
  }

  /** What to type instead, with the profile's own example (never a number written here). */
  phoneHint(): string {
    const example = phoneExample(this.regional.settings().phone);
    return example
      ? this.i18n.t('cust.phone.notMobile', { local: isolate(example.local), international: isolate(example.international) })
      : this.i18n.t('cust.phone.notMobileShort');
  }

  /** A stored phone as it reads on screen: +213 658 41 77 30. */
  phoneShown(phone: string): string {
    return formatPhone(phone, this.regional.settings().phone);
  }

  day(value: string | null): string {
    return formatDay(value, this.i18n.lang(), this.regional.settings().timeZone);
  }

  /** Today in the company's own zone — the day a takeover is dated by default. */
  private today(): string {
    return localDay(new Date().toISOString(), this.regional.settings().timeZone);
  }

  /** The latest day a paper can have been issued on; guides the picker only. */
  todayDay(): string {
    return this.today();
  }

  /** An issue date is a day with no zone: read at midday so no offset moves it. */
  issuedDay(day: string | null | undefined): string {
    return day ? this.day(`${day.slice(0, 10)}T12:00:00Z`) : '—';
  }

  chooseKind(kind: CustomerKind): void {
    this.carryName(this.kind(), kind);
    this.kind.set(kind);
    this.touched.touch('kind');
    // A change block belongs to a company; a chip that leaves that state closes it.
    if (kind !== 'Company') this.changing.set(false);
    // A new customer switching kind before the first save keeps what both kinds share — the
    // name, phone, address, email, birth date, credit limit — and drops what only the other kind
    // has (owner, 2026-10-05), with its complaints, so switching back starts those fields empty:
    // a person has no tax number and no representative, a company no identity paper of its own.
    // A stored row keeps the form's values — its save decides what is sent (taxIdToSend).
    if (this.editing() !== null) return;
    if (kind === 'Person') {
      this.form.taxId = null;
      this.touched.forget('taxId');
      this.clearRepresentativeFields();
      this.touched.forget('repFullName');
    } else if (kind === 'Company') {
      this.idDocumentNumber.set('');
      this.idDocumentType.set('');
      this.idDocumentIssuer.set('');
      this.idDocumentIssuedOn.set('');
    }
  }

  /**
   * The name follows a change of kind, for a new customer and a stored row alike: a person's two
   * parts become the one name of a company, first name first; one name becomes a person's family
   * name while no first name is typed — where the migration filed everyone already on file — so
   * nothing typed is lost and the clerk moves the first name out. The same kind again moves nothing.
   */
  private carryName(from: CustomerKind, to: CustomerKind): void {
    if (from === to) return;
    if (to === 'Person') {
      if (!this.form.firstName.trim()) this.form.familyName = this.form.fullName;
    } else if (from === 'Person') {
      this.form.fullName = composeName(this.form.firstName, this.form.familyName) || this.form.fullName;
    }
  }

  /**
   * A person whose whole name still sits under the family name — filed before the two parts
   * existed, or until now a company or undecided — and no first name typed yet.
   */
  nameToSplit(): boolean {
    const row = this.editing();
    return this.kind() === 'Person' && row !== null && !row.firstName && !this.form.firstName.trim();
  }

  /**
   * The first answer of a new customer, from the two large choices: the small dialog grows into
   * the editor with that kind's fields, and the cursor goes to the first one to type into — the
   * name — once it has grown.
   */
  pickKind(kind: CustomerKind): void {
    const panel = this.editorPanel()?.nativeElement;
    const from = panel?.getBoundingClientRect();
    this.chooseKind(kind);
    afterNextRender(() => this.grow(panel, from), { injector: this.injector });
  }

  /**
   * The dialog's size change, from the small question to the editor (owner, 2026-10-05: "it opens
   * smoothly with the inputs"): its inline and block size go from what they were to what the
   * fields need, over the system's resize length (--t-slide), and the fields fade in over the same
   * time — no other motion. Under prefers-reduced-motion, or where the browser cannot animate, the
   * editor is simply there. Measured rather than written as CSS, because a box sized by its
   * content has no height a transition could start from.
   */
  private grow(panel: HTMLElement | undefined, from: DOMRect | undefined): void {
    const focusName = () => panel?.querySelector<HTMLInputElement>('#cu-first-name, #cu-name')?.focus();
    const duration = panel && from && typeof panel.animate === 'function' ? slideMs(panel) : 0;
    if (!panel || !from || duration === 0) {
      focusName();
      return;
    }
    const to = panel.getBoundingClientRect();
    const size = (r: DOMRect) => ({ inlineSize: `${r.width}px`, blockSize: `${r.height}px` });
    const growth = panel.animate([size(from), size(to)], { duration, easing: 'ease' });
    panel.querySelector('.bo-panel__body')?.animate([{ opacity: 0 }, { opacity: 1 }], { duration, easing: 'ease' });
    growth.finished.then(focusName, focusName);
  }

  /** Where a new customer's dialog puts the cursor once it has drawn: the first of the two choices. */
  private focusFirstChoice(): void {
    afterNextRender(() => this.editorPanel()?.nativeElement.querySelector<HTMLElement>('#cu-pick-person')?.focus(), {
      injector: this.injector,
    });
  }

  /** The legacy person's number goes: sent blank on save, and gone from the form if the kind changes back. */
  removeTaxId(): void {
    this.taxIdRemoved.set(true);
    this.form.taxId = null;
  }

  /**
   * What the save sends as the tax number. A person sends none — except a row that was a person
   * already and carries one from before the rule, which is sent back unchanged until it is
   * removed. Everyone else sends what is typed.
   */
  private taxIdToSend(): string | null {
    if (this.kind() === 'Person') return this.keepsPersonTaxId() ? this.storedPersonTaxId() : null;
    return this.form.taxId?.trim() || null;
  }

  /** A new customer has to choose; an unspecified row being edited may stay silent (the warn chip speaks). */
  kindError(): boolean {
    return kindProblem(this.editing() === null, this.editing()?.kind ?? 'Unspecified', this.kind()) === 'kindRequired'
      && this.touched.missing('kind', '');
  }

  repNameMissing(): boolean {
    return this.touched.missing('repFullName', this.repFullName());
  }

  /**
   * What is filled in and wrong on the customer's own paper (owner, 2026-10-02: optional, but
   * correct when filled). A company keeps no paper of its own, so nothing is checked for one.
   */
  ownIdProblems(): Partial<Record<IdentityPart, string>> {
    if (this.kind() === 'Company') return {};
    return identityProblems(
      {
        idNumber: this.idDocumentNumber(),
        idIssuer: this.idDocumentIssuer(),
        idIssuedOn: this.idDocumentIssuedOn(),
        birthDate: this.form.birthDate ?? '',
      },
      this.today(),
    );
  }

  /** The same for the representative's paper; a representative's row holds no birth date. */
  repIdProblems(): Partial<Record<IdentityPart, string>> {
    return identityProblems(
      { idNumber: this.repIdDocument(), idIssuer: this.repIdIssuer(), idIssuedOn: this.repIdIssuedOn() },
      this.today(),
    );
  }

  canSave(): boolean {
    // A person needs both parts of the name (owner, 2026-10-06); anyone else the one name.
    if (this.kind() === 'Person') {
      if (!this.form.firstName.trim() || !this.form.familyName.trim()) return false;
    } else if (!this.form.fullName.trim()) return false;
    if (Object.keys(this.ownIdProblems()).length || Object.keys(this.repIdProblems()).length) return false;
    if (kindProblem(this.editing() === null, this.editing()?.kind ?? 'Unspecified', this.kind()) !== null) return false;
    if (this.needsRep() && !this.repFullName().trim()) return false;
    if (this.req.phone && !this.form.phone?.trim()) return false;
    if (this.phoneNotMobile(this.form.phone, this.editing()?.phone) || this.phoneNotMobile(this.repPhone())) return false;
    if (this.req.email && !this.form.email?.trim()) return false;
    if (this.req.birthDate && !this.form.birthDate) return false;
    if (this.req.address && !this.form.address?.trim()) return false;
    if (this.taxIdRequired() && !this.form.taxId?.trim()) return false;
    return true;
  }

  /** Asks again for the page on screen, after a save or a delete. */
  async reload(): Promise<void> {
    if (this.createOnly()) return;
    await this.pager.reload();
  }

  /** One customer as the server now holds it, whatever page it is on. */
  private async freshRow(id: string): Promise<AdminCustomer | null> {
    try {
      return (await this.admin.customersPage({ skip: 0, take: 1, id })).items[0] ?? null;
    } catch {
      return null;
    }
  }

  openCreate(): void {
    this.resetEditor();
    this.notice.set(null);
    this.editorOpen.set(true);
    this.focusFirstChoice();
  }

  edit(c: AdminCustomer): void {
    this.editing.set(c);
    this.touched.reset();
    this.notice.set(null);
    this.changing.set(false);
    this.taxIdRemoved.set(false);
    this.kind.set(c.kind);
    this.idDocumentNumber.set(c.idDocumentNumber ?? '');
    this.idDocumentType.set(c.idDocumentType ?? '');
    this.idDocumentIssuer.set(c.idDocumentIssuer ?? '');
    this.idDocumentIssuedOn.set(c.idDocumentIssuedOn ? c.idDocumentIssuedOn.slice(0, 10) : '');
    this.clearRepresentativeFields();
    this.form = {
      fullName: c.fullName,
      firstName: c.firstName ?? '',
      // A person filed before the parts existed has the whole name here; a server older than the
      // parts sends none, and the whole name goes where the migration files it too.
      familyName: c.familyName ?? (c.firstName ? '' : c.fullName),
      // Shown as it reads (+213 658 41 77 30); a save reads it back to the same stored number.
      phone: c.phone ? formatPhone(c.phone, this.regional.settings().phone) : c.phone,
      address: c.address,
      taxId: c.taxId,
      email: c.email,
      birthDate: c.birthDate ? c.birthDate.slice(0, 10) : null,
      creditLimit: c.creditLimit,
    };
    this.fiscalIdValues = { ...(c.fiscalIds ?? {}) };
    this.clearScans();
    this.editorOpen.set(true);
    if (c.kind === 'Company') void this.loadHistory(c.id);
    else this.history.set([]);
    if (this.canSeeScans()) void this.loadScans(c.id);
  }

  /** Two clicks and a name: the row's own action opens the edit with the change block showing. */
  changeFromRow(c: AdminCustomer): void {
    this.edit(c);
    this.openChange();
  }

  /** The reader leaves the editor: Cancel, the backdrop or Escape. */
  cancelEdit(): void {
    this.resetEditor();
    if (this.createOnly()) this.closed.emit();
  }

  private resetEditor(): void {
    this.editorOpen.set(false);
    this.editing.set(null);
    this.openingDraft.set(emptyOpeningDraft(localToday()));
    this.touched.reset();
    this.changing.set(false);
    this.taxIdRemoved.set(false);
    this.kind.set('Unspecified');
    this.idDocumentNumber.set('');
    this.idDocumentType.set('');
    this.idDocumentIssuer.set('');
    this.idDocumentIssuedOn.set('');
    this.clearRepresentativeFields();
    this.history.set([]);
    this.clearScans();
    this.form = this.emptyForm();
    this.fiscalIdValues = {};
  }

  private clearScans(): void {
    this.scans.set([]);
    this.pendingScans.set([]);
    this.scanError.set(null);
  }

  private async loadScans(customerId: string): Promise<void> {
    this.scansLoading.set(true);
    try {
      const rows = await this.admin.listCustomerAttachments(customerId);
      // The edit may have moved on to another row while this was in flight.
      if (this.editingId() === customerId) this.scans.set(rows);
    } catch (e) {
      if (this.editingId() === customerId) this.scanError.set(adminErrorMessage(e));
    } finally {
      this.scansLoading.set(false);
    }
  }

  /** The limits the server enforces, checked first so a phone photo too large is said at once. */
  chooseScans(event: Event): void {
    const input = event.target as HTMLInputElement;
    const files = Array.from(input.files ?? []);
    input.value = '';
    this.scanError.set(null);
    const accepted: File[] = [];
    const room = this.scanRoom();
    for (const file of files) {
      if (file.type && !SCAN_TYPES.includes(file.type)) {
        this.scanError.set(this.i18n.t('cust.scan.wrongType'));
        continue;
      }
      if (file.size > SCAN_MAX_BYTES) {
        this.scanError.set(this.i18n.t('cust.scan.tooLarge'));
        continue;
      }
      if (accepted.length >= room) {
        this.scanError.set(this.i18n.t('cust.scan.max', { n: MAX_SCANS }));
        break;
      }
      accepted.push(file);
    }
    if (accepted.length) this.pendingScans.update((list) => [...list, ...accepted]);
  }

  unqueueScan(index: number): void {
    this.pendingScans.update((list) => list.filter((_, i) => i !== index));
  }

  /** Opened from a blob in a new tab: the file needs the session's token, which a plain link would not carry. */
  async viewScan(file: CustomerAttachment): Promise<void> {
    const id = this.editingId();
    if (!id) return;
    this.scanError.set(null);
    this.scanBusy.set(true);
    // Opened now, while the click still counts as the reader's: a window opened after the file has
    // arrived is a popup the browser blocks. It is cut from this page before anything loads in it.
    const shown = window.open('', '_blank');
    if (shown) shown.opener = null;
    try {
      const url = URL.createObjectURL(await this.admin.customerAttachmentBlob(id, file.id));
      if (shown) shown.location.href = url;
      else window.open(url, '_blank', 'noopener');
      setTimeout(() => URL.revokeObjectURL(url), 60_000);
    } catch (e) {
      shown?.close();
      this.scanError.set(adminErrorMessage(e));
    } finally {
      this.scanBusy.set(false);
    }
  }

  async removeScan(file: CustomerAttachment): Promise<void> {
    const id = this.editingId();
    if (!id) return;
    const yes = await this.confirm.ask({
      ...deleteRequest(this.i18n, this.i18n.t('cust.scan.removeTitle', { name: file.fileName }), this.i18n.t('cust.scan.removeMessage')),
      confirmLabel: this.i18n.t('cust.scan.remove'),
    });
    if (!yes) return;
    this.scanError.set(null);
    this.scanBusy.set(true);
    try {
      await this.admin.removeCustomerAttachment(id, file.id);
      this.scans.update((list) => list.filter((x) => x.id !== file.id));
    } catch (e) {
      this.scanError.set(adminErrorMessage(e));
    } finally {
      this.scanBusy.set(false);
    }
  }

  sizeOf(bytes: number): string {
    return this.i18n.t('cust.scan.kb', { n: Math.max(1, Math.round(bytes / 1024)) });
  }

  /**
   * The chosen files, sent once the customer exists. One that fails does not undo the save: the
   * customer is on file, and the sentence says which copy to add again from the edit.
   */
  private async sendScans(customerId: string, files: File[]): Promise<string | null> {
    for (const file of files) {
      try {
        await this.admin.uploadCustomerAttachment(customerId, file);
      } catch (e) {
        return this.i18n.t('cust.scan.uploadFailed', { name: file.name, reason: adminErrorMessage(e) });
      }
    }
    return null;
  }

  private clearRepresentativeFields(): void {
    this.repFullName.set('');
    this.repIdDocument.set('');
    this.repPhone.set('');
    this.repNote.set('');
    this.repIdType.set('');
    this.repIdIssuer.set('');
    this.repIdIssuedOn.set('');
  }

  /** The representative's paper as the request carries it, for the first appointment and a change alike. */
  private repPaper(): { idDocumentType: IdDocumentType | null; idDocumentIssuer: string | null; idDocumentIssuedOn: string | null } {
    return {
      idDocumentType: idDocumentTypeOrNull(this.repIdType()),
      idDocumentIssuer: this.repIdIssuer().trim() || null,
      idDocumentIssuedOn: this.repIdIssuedOn() || null,
    };
  }

  /** Loaded only when a company's edit opens; the list itself needs nothing but the current one. */
  private async loadHistory(customerId: string): Promise<void> {
    this.historyLoading.set(true);
    try {
      const rows = await this.admin.listRepresentatives(customerId);
      // The edit may have moved on to another row while this was in flight.
      if (this.editingId() === customerId) this.history.set(rows);
    } catch (e) {
      this.error.set(adminErrorMessage(e));
    } finally {
      this.historyLoading.set(false);
    }
  }

  openChange(): void {
    this.clearRepresentativeFields();
    const today = this.today();
    this.changeMax.set(today);
    this.changeFrom.set(today);
    this.notice.set(null);
    this.changing.set(true);
  }

  cancelChange(): void {
    this.changing.set(false);
  }

  /**
   * Not the customer PUT: the company row does not change, the history does. The server ends
   * the current representative at the takeover instant and adds the new one in one write, and
   * answers with the whole history, so the table below redraws from that one response.
   */
  async confirmChange(): Promise<void> {
    const c = this.editing();
    const fullName = this.repFullName().trim();
    if (!c || !fullName || this.changeFromProblem() !== null) return;

    if (!(await this.confirm.ask(actionRequest(this.i18n, this.i18n.t('cust.rep.confirm'))))) return;

    this.busy.set(true);
    this.error.set(null);
    this.notice.set(null);
    try {
      // Today means now; an earlier day means its start, floored at the current one's own start
      // so a same-day takeover is never dated before them. The server allows a day of slack
      // for the zone gap, so a company-zone "today" is never read as tomorrow.
      const fromUtc = takeoverInstant(this.changeFrom(), this.changeMax(), c.representative?.fromUtc);
      const rows = await this.admin.changeRepresentative(c.id, {
        fullName,
        idDocumentNumber: this.repIdDocument().trim() || null,
        phone: this.repPhone().trim() || null,
        note: this.repNote().trim() || null,
        fromUtc,
        ...this.repPaper(),
      });
      this.history.set(rows);
      this.changing.set(false);
      this.clearRepresentativeFields();
      this.notice.set(this.i18n.t('cust.rep.changed'));
      await this.reload();
      // The row being edited must show the new current one, not the one it was opened with.
      const fresh = await this.freshRow(c.id);
      if (fresh) this.editing.set(fresh);
    } catch (e) {
      this.error.set(adminErrorMessage(e));
    } finally {
      this.busy.set(false);
    }
  }

  async save(): Promise<void> {
    this.touched.touchAll(
      'fullName', 'firstName', 'familyName', 'taxId', 'phone', 'email', 'birthDate', 'address', 'kind', 'repFullName',
    );
    if (!this.canSave()) return;
    if (!this.editingId() && this.openingInvalid()) return;

    const person = this.kind() === 'Person';
    const fullNameForConfirm = person ? composeName(this.form.firstName, this.form.familyName) : this.form.fullName.trim();
    if (!(await this.confirm.ask(saveRequest(this.i18n, fullNameForConfirm)))) return;

    this.busy.set(true);
    this.error.set(null);
    this.notice.set(null);
    try {
      const id = this.editingId();
      const kind = this.kind();
      const person = kind === 'Person';
      const firstName = this.form.firstName.trim();
      const familyName = this.form.familyName.trim();
      const payload: UpsertCustomerRequest = {
        ...this.form,
        // A person's two parts, and the full name composed first name first for a server older than
        // them (a current one composes it itself); anyone else one name and no parts.
        fullName: person ? composeName(firstName, familyName) : this.form.fullName.trim(),
        firstName: person ? firstName : null,
        familyName: person ? familyName : null,
        phone: this.form.phone?.trim() || null,
        address: this.form.address?.trim() || null,
        taxId: this.taxIdToSend(),
        fiscalIds: customerFiscalIdsPayload(kind, this.otherFiscalIds(), this.fiscalIdValues),
        email: this.form.email?.trim() || null,
        birthDate: this.form.birthDate || null,
        kind,
        // A company carries no document of its own; its representative does.
        idDocumentNumber: kind === 'Company' ? null : this.idDocumentNumber().trim() || null,
        idDocumentType: kind === 'Company' ? null : idDocumentTypeOrNull(this.idDocumentType()),
        idDocumentIssuer: kind === 'Company' ? null : this.idDocumentIssuer().trim() || null,
        idDocumentIssuedOn: kind === 'Company' ? null : this.idDocumentIssuedOn() || null,
        // The first representative rides in the same save; never a second one (that is a change).
        representative: this.needsRep()
          ? {
              fullName: this.repFullName().trim(),
              idDocumentNumber: this.repIdDocument().trim() || null,
              phone: this.repPhone().trim() || null,
              note: this.repNote().trim() || null,
              ...this.repPaper(),
            }
          : null,
      };
      // Taken before the save resets the editor; a company's form sends none.
      const scans = this.showsScans() ? this.pendingScans() : [];
      // No code in either request: the server draws it on create and never changes it after.
      let drawn: string | null = null;
      let scanProblem: string | null = null;
      if (id) {
        await this.admin.updateCustomer(id, payload);
        scanProblem = await this.sendScans(id, scans);
      } else {
        // The branch whose code goes into the new customer's (owner, 2026-10-05): the host's when
        // it has one (a rental contract's), else the Admin's working branch, else their own; a
        // locked user's own. The server decides either way.
        const branchId = this.atBranch() ?? this.branchScope.resolve('oneBranch').branchId;
        const opening = this.createOnly() ? null : openingPayload(this.openingDraft());
        const saved = await this.admin.createCustomer({ ...payload, branchId, openingBalance: opening });
        scanProblem = await this.sendScans(saved.id, scans);
        if (this.createOnly()) {
          this.resetEditor();
          this.created.emit(saved);
          return;
        }
        drawn = saved.code;
      }
      this.resetEditor();
      await this.reload();
      // The code exists only now that the save has answered; the clerk hears which one it is.
      if (drawn) this.notice.set(this.i18n.t('cust.code.created', { code: isolate(drawn) }));
      if (scanProblem) this.error.set(scanProblem);
    } catch (e) {
      this.error.set(this.saveError(e));
    } finally {
      this.busy.set(false);
    }
  }

  /**
   * The server's refusal of a save in the reader's words when it carries a code this screen knows
   * (CustomerRules.TaxIdCompanyOnly); otherwise its own sentence, as before.
   */
  private saveError(error: unknown): string {
    if (problemCode(error) === TAX_ID_COMPANY_ONLY) return this.i18n.t('cust.taxId.companyOnly');
    if (problemCode(error) === PHONE_NOT_MOBILE) return this.phoneHint();
    return adminErrorMessage(error);
  }

  /**
   * After the opening balance was set or corrected on its own call: the list and the open row show
   * the balance the server now holds. The rest of the editor is left as the clerk had it.
   */
  async openingChanged(fresh: OpeningParty | null): Promise<void> {
    await this.reload();
    const id = this.editingId();
    const row = (id ? await this.freshRow(id) : null) ?? (fresh as AdminCustomer | null);
    if (row) this.editing.set(row);
    this.notice.set(this.i18n.t(fresh ? 'cust.opening.saved' : 'cust.opening.correction.saved'));
  }

  async remove(c: AdminCustomer): Promise<void> {
    const title = this.i18n.t('customers.delete.title', { name: c.fullName || c.code });
    if (!(await this.confirm.ask(deleteRequest(this.i18n, title)))) return;
    this.busy.set(true);
    this.error.set(null);
    try {
      await this.admin.deleteCustomer(c.id);
      await this.reload();
    } catch (e) {
      this.error.set(adminErrorMessage(e));
    } finally {
      this.busy.set(false);
    }
  }
}

/** What the server keeps with a customer: four types, 5 MB, ten files (CustomerAttachments). */
const SCAN_TYPES = ['image/jpeg', 'image/png', 'image/webp', 'application/pdf'];
const SCAN_MAX_BYTES = 5 * 1024 * 1024;
const MAX_SCANS = 10;

/** The full name as it prints: the first name, then the family name (owner, 2026-10-06). */
function composeName(firstName: string, familyName: string): string {
  return [firstName.trim(), familyName.trim()].filter(Boolean).join(' ');
}
