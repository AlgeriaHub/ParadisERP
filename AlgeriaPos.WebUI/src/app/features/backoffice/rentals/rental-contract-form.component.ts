import { identityProblems, localToday } from '../../../core/identity';
import { Component, ElementRef, ViewChild, OnInit, computed, effect, inject, input, output, signal } from '@angular/core';
import { InvalidSummaryComponent } from '../../../ui/invalid-summary.component';
import { FoldComponent } from '../../../ui/fold.component';
import { ConfirmService } from '../../../ui/confirm.service';
import { differsFromBlank, removeLineRequest, saveRequest } from '../../../ui/confirm-requests';
import { AuthService } from '../../../core/auth.service';
import { TaxMode } from '../../../core/models';
import { ConnectivityService } from '../../../core/connectivity.service';
import { canCreateCustomer } from '../../../core/customer-access';
import { QuickCustomerComponent } from '../quick-customer.component';
import { DateInputComponent } from '../../../ui/date-input.component';
import { PageChrome } from '../shell/page-chrome';
import { FormsModule } from '@angular/forms';
import { AdminBranch, AdminCustomer, AdminProduct, AdminService, adminErrorMessage } from '../../../core/admin.service';
import { BranchLock } from '../../../core/branch-lock';
import { I18nService } from '../../../core/i18n.service';
import { RegionalService } from '../../../core/regional.service';
import { ID_DOCUMENT_TYPES, damagePriceFor, multiplierFor, overdueUnitPrice, rentalAmount } from '../../../core/rentals';
import { localizedName } from '../backoffice-format';
import { TouchedFields } from '../backoffice-forms';
import { ProductPickerComponent } from '../../../shared/product-picker.component';
import { CatalogStore } from '../../../core/catalog-store';
import { SearchableProduct } from '../../../core/product-search';
import { CustomerSite, RentalContractLineRequest, RentalSettings, RentalsApi, UpsertRentalContractRequest } from './rentals-api.service';
import { ContractWording, StoreDocumentsApi } from '../../print/store-documents-api.service';
import {
  IdentifiedCustomer,
  IdentifiedRepresentative,
  SignatoryPrefill,
  fromRecord,
  representativeChoices,
  signatoryHintKey,
  signatoryMode,
  signatoryPrefill,
} from './rental-signatory';
import { today } from './rental-view';
import { BuilderCopyKey, builderCopy } from './rental-builder-copy';

/** One line being written. `parentKey` points at another draft line, since none has an id yet. */
interface DraftLine {
  key: number;
  productId: string | null;
  qty: number;
  durationDays: number;
  rentUnitPrice: number;
  damageUnitPrice: number;
  /** Null lets the server derive it from the multiplier in force — including the item's own. */
  overdueUnitPrice: number | null;
  parentKey: number | null;
}

/**
 * Raising a contract — the document the customer signs.
 *
 * <p>Three prices per line get signed, not one: the hire, what a unit costs if it comes back
 * broken or not at all, and what a day past the period costs. The overdue price has to be on the
 * paper with the others or there is nothing to stand on when the customer refuses the
 * difference — which is why it is shown here rather than computed silently at the end. The
 * figure the multiplier gives — the item's own when it has one, read from the server — is a
 * **floor**: a negotiated price may go up from it, never down, and the form says so before the
 * server refuses it.</p>
 *
 * <p>When the deposit switch is on, the deposit is one more figure on the paper: entered here,
 * frozen at the first issue note with the prices, shown while it is held and stated as owed back
 * at close. It is never netted against what the contract charges.</p>
 *
 * <p>The total is the full agreed period on every line, because that is what the customer is
 * invoiced the moment the goods go out. Showing it while the contract is still being typed is
 * the only point at which anyone can still change their mind about it.</p>
 *
 * <p>The paper also names who signed it, and nobody signs on a customer's behalf (answer 11). A
 * person signs for themselves, so the name is the customer's; a company signs through a
 * representative chosen from its history — the current one by default. The identity the paper
 * records (answer 10: document type, number, issuer, issue date, birth date) comes from the same
 * record, and only what the record lacks is typed here. It may stay incomplete on a draft; the
 * first issue note is refused until it is whole.</p>
 *
 * <p>And it names the site the goods go to (answer 9: mandatory, one customer may have several),
 * chosen from the customer's sites or added here in the same breath, and the lorry if one is
 * already known (answer 12) — each note carries its own truck as well.</p>
 */
@Component({
  selector: 'app-rental-contract-form',
  standalone: true,
  imports: [FoldComponent, FormsModule, ProductPickerComponent, InvalidSummaryComponent, QuickCustomerComponent, DateInputComponent],
  template: `
    <div class="bo-stack contract-form">
      @if (error()) {
        <p class="notice danger" role="alert" tabindex="-1" id="rf-server-error">{{ error() }}</p>
      }

      @if (quickCustomer()) {
        <!-- The customer is created at the contract's branch (owner, 2026-10-05): its code carries it. -->
        <app-quick-customer [branchId]="branchId()" (created)="customerCreated($event)" (closed)="quickCustomer.set(false)" />
      }

      <!-- After a save that the form refused: what to fix, each a step to its field. -->
      <app-invalid-summary [scope]="formEl" [attempt]="attempts()" />

      @if (attempts() && invalidLines().length) {
        <div class="notice danger" role="alert" tabindex="-1" id="rf-line-errors">
          <strong>{{ i18n.t('ui.errors.title') }}</strong>
          @for (line of invalidLines(); track line.key) {
            <button type="button" class="line-error-link" (click)="editLine(line.key, true)">{{ lineName(line) }}: {{ ui('review') }}</button>
          }
        </div>
      }

      <!-- The title is the page header's; the one Cancel is in the action bar below. Four sections
           (owner, 2026-10-06): the customer and the site, who signs, one note on what the paper
           holds, the contract's own details — then the lines. Each titled section folds (owner,
           2026-10-08); one holding a required field opens, the lines are never folded. -->
      <section class="bo-panel form-panel" #formEl>
        <div class="bo-panel__body contract-body">
          <app-fold class="contract-section" [heading]="i18n.t('rent.form.who')" [open]="true">
            <div class="contract-grid">
              <div class="bo-field contract-customer contract-wide" [class.is-invalid]="touched.missing('customer', customerId())">
                <label for="rf-customer">
                  {{ i18n.t('common.customer') }}<span class="bo-req">{{ i18n.t('common.required') }}</span>
                </label>
                <div class="contract-pick">
                  <select
                    id="rf-customer"
                    [ngModel]="customerId()"
                    (ngModelChange)="pickCustomer($event)"
                    (blur)="touched.touch('customer')"
                  >
                    <option [ngValue]="null">—</option>
                    @for (c of allCustomers(); track c.id) {
                      <option [ngValue]="c.id">{{ c.fullName }}</option>
                    }
                  </select>
                  <!-- FR-1: a customer met for the first time is created here, without leaving the
                       contract; offered only to whom the server lets create one, and only online. -->
                  @if (mayCreateCustomer()) {
                    <button type="button" class="btn-ghost bo-small new-customer" (click)="quickCustomer.set(true)">
                      <span aria-hidden="true">+</span> {{ i18n.t('backoffice.customers.new') }}
                    </button>
                  }
                </div>
                @if (touched.missing('customer', customerId())) {
                  <span class="bo-error" role="alert">{{ i18n.t('common.fieldRequired') }}</span>
                }
              </div>
              <div class="bo-field contract-site contract-wide" [class.is-invalid]="siteInvalid()">
                <label for="rf-site">
                  {{ i18n.t('rent.site.label') }}
                  <span class="bo-req" [class.is-optional]="!siteRequired()">{{ i18n.t(siteRequired() ? 'common.required' : 'common.optional') }}</span>
                </label>
                <div class="contract-pick">
                  <select
                    id="rf-site"
                    [disabled]="!customerId()"
                    [ngModel]="siteId()"
                    (ngModelChange)="siteId.set($event)"
                    (blur)="touched.touch('site')"
                  >
                    <option [ngValue]="null">—</option>
                    @for (s of siteChoices(); track s.id) {
                      <option [ngValue]="s.id">{{ s.name }}{{ s.isActive ? '' : ' — ' + i18n.t('rent.site.closed') }}</option>
                    }
                  </select>
                  @if (customerId()) {
                    <button type="button" class="btn-ghost bo-small" (click)="addingSite.set(!addingSite())">
                      {{ i18n.t(addingSite() ? 'common.cancel' : 'rent.site.new') }}
                    </button>
                  }
                </div>
                @if (!siteId() && !siteInvalid()) { <p class="bo-hint">{{ i18n.t('rent.site.required') }}</p> }
                @if (siteInvalid()) {
                  <span class="bo-error" role="alert">{{ i18n.t('rent.site.required') }}</span>
                }
              </div>
              @if (addingSite() && customerId()) {
                <div class="contract-new-site contract-wide">
                  <div class="contract-grid">
                    <div class="bo-field" [class.is-invalid]="touched.missing('siteName', newSiteName().trim())">
                      <label for="rf-site-name">
                        {{ i18n.t('rent.site.name') }}<span class="bo-req">{{ i18n.t('common.required') }}</span>
                      </label>
                      <input
                        id="rf-site-name"
                        maxlength="120"
                        [ngModel]="newSiteName()"
                        (ngModelChange)="newSiteName.set($event)"
                        (blur)="touched.touch('siteName')"
                      />
                      @if (touched.missing('siteName', newSiteName().trim())) {
                        <span class="bo-error" role="alert">{{ i18n.t('common.fieldRequired') }}</span>
                      }
                    </div>
                    <div class="bo-field">
                      <label for="rf-site-phone">
                        {{ i18n.t('rent.site.phone') }}<span class="bo-req is-optional">{{ i18n.t('common.optional') }}</span>
                      </label>
                      <input id="rf-site-phone" class="mono" dir="ltr" maxlength="40" [ngModel]="newSitePhone()" (ngModelChange)="newSitePhone.set($event)" />
                    </div>
                    <div class="bo-field contract-wide">
                      <label for="rf-site-address">
                        {{ i18n.t('rent.site.address') }}<span class="bo-req is-optional">{{ i18n.t('common.optional') }}</span>
                      </label>
                      <input id="rf-site-address" maxlength="300" [ngModel]="newSiteAddress()" (ngModelChange)="newSiteAddress.set($event)" />
                    </div>
                  </div>
                  <div class="contract-new-site__foot">
                    <p class="bo-hint">{{ i18n.t('rent.site.newHint') }}</p>
                    <button type="button" class="btn-ghost" [disabled]="busy()" (click)="createSite()">
                      {{ i18n.t('rent.site.add') }}
                    </button>
                  </div>
                </div>
              }
            </div>
          </app-fold>

          <!-- Who signs, in a container of its own: the signatory's identity is one block, so a
               later choice of who signs can show or hide it whole. -->
          <app-fold class="contract-section contract-signatory" [heading]="i18n.t('rent.sign.title')" [open]="true">
            <!-- A person signs himself or through an agent (owner, 2026-10-06). Himself, his paper is
                 on his own record and is not asked here; an agent's is typed. -->
            @if (mode() === 'person') {
              <div class="signs-choice" role="radiogroup" aria-labelledby="rf-signs-title">
                <span class="signs-choice__label" id="rf-signs-title">{{ i18n.t('rent.sign.who') }}</span>
                <div class="bo-check">
                  <input id="rf-signs-self" type="radio" name="rf-signs" [checked]="!byAgent()" (change)="chooseSigner(false)" />
                  <label for="rf-signs-self">{{ i18n.t('rent.sign.self') }}</label>
                </div>
                <div class="bo-check">
                  <input id="rf-signs-agent" type="radio" name="rf-signs" [checked]="byAgent()" (change)="chooseSigner(true)" />
                  <label for="rf-signs-agent">{{ i18n.t('rent.sign.agent') }}</label>
                </div>
              </div>
            }
            <div class="contract-grid">
              <div class="bo-field contract-signatory-name">
                <label for="rf-signatory">
                  {{ i18n.t(agentSigns() ? 'rent.sign.agentName' : 'rent.sign.name') }}<span class="bo-req">{{ i18n.t('common.required') }}</span>
                </label>
                @switch (mode()) {
                  @case ('company') {
                    <select id="rf-signatory" [ngModel]="representativeId()" (ngModelChange)="pickRepresentative($event)">
                      <option [ngValue]="null">—</option>
                      @for (r of representatives(); track r.id) {
                        <option [ngValue]="r.id">
                          {{ r.fullName }}{{ r.toUtc ? ' — ' + i18n.t('rent.sign.former') : '' }}
                        </option>
                      }
                    </select>
                  }
                  @case ('person') {
                    @if (agentSigns()) {
                      <input id="rf-signatory" maxlength="200" [ngModel]="signatoryName()" (ngModelChange)="signatoryName.set($event)" />
                    } @else {
                      <input id="rf-signatory" maxlength="200" disabled [ngModel]="signatoryName()" />
                    }
                  }
                  @default {
                    <input
                      id="rf-signatory"
                      maxlength="200"
                      [disabled]="!customerId()"
                      [ngModel]="signatoryName()"
                      (ngModelChange)="signatoryName.set($event)"
                    />
                  }
                }
              </div>
              @if (!selfSigns()) {
              <div class="bo-field contract-signatory-type">
                <label for="rf-id-type">{{ i18n.t('rent.id.type') }}<span class="bo-req">{{ i18n.t('common.required') }}</span></label>
                <select
                  id="rf-id-type"
                  [disabled]="locked('idType')"
                  [ngModel]="identity().idType"
                  (ngModelChange)="patchIdentity({ idType: $event })"
                >
                  <option value="">—</option>
                  @for (type of idTypes; track type) {
                    <option [value]="type">{{ i18n.t('rent.id.kind.' + type) }}</option>
                  }
                </select>
              </div>
              <div class="bo-field contract-signatory-id" [class.is-invalid]="!!identityProblem().idNumber">
                <label for="rf-signatory-id">
                  {{ i18n.t('rent.sign.idNumber') }}<span class="bo-req">{{ i18n.t('common.required') }}</span>
                </label>
                <input
                  id="rf-signatory-id"
                  class="mono"
                  dir="ltr"
                  maxlength="40"
                  [disabled]="locked('idNumber')"
                  [ngModel]="identity().idNumber"
                  (ngModelChange)="patchIdentity({ idNumber: $event })"
                />
                @if (identityProblem().idNumber; as key) {
                  <span class="bo-error" role="alert">{{ i18n.t(key) }}</span>
                }
              </div>
              <div class="bo-field contract-signatory-issuer" [class.is-invalid]="!!identityProblem().idIssuer">
                <label for="rf-id-issuer">{{ i18n.t('rent.id.issuer') }}<span class="bo-req is-optional">{{ i18n.t('common.optional') }}</span></label>
                <input
                  id="rf-id-issuer"
                  maxlength="120"
                  [disabled]="locked('idIssuer')"
                  [ngModel]="identity().idIssuer"
                  (ngModelChange)="patchIdentity({ idIssuer: $event })"
                />
                @if (identityProblem().idIssuer; as key) {
                  <span class="bo-error" role="alert">{{ i18n.t(key) }}</span>
                }
              </div>
              <div class="bo-field contract-signatory-issued" [class.is-invalid]="!!identityProblem().idIssuedOn">
                <label for="rf-id-issued">{{ i18n.t('rent.id.issuedOn') }}<span class="bo-req is-optional">{{ i18n.t('common.optional') }}</span></label>
                <app-date-input
                  id="rf-id-issued"
                  [disabled]="locked('idIssuedOn')"
                  [ngModel]="identity().idIssuedOn"
                  (ngModelChange)="patchIdentity({ idIssuedOn: $event })"
                />
                @if (identityProblem().idIssuedOn; as key) {
                  <span class="bo-error" role="alert">{{ i18n.t(key) }}</span>
                }
              </div>
              <div class="bo-field contract-signatory-born" [class.is-invalid]="!!identityProblem().birthDate">
                <label for="rf-id-born">{{ i18n.t('rent.id.birthDate') }}<span class="bo-req is-optional">{{ i18n.t('common.optional') }}</span></label>
                <app-date-input
                  id="rf-id-born"
                  [disabled]="locked('birthDate')"
                  [ngModel]="identity().birthDate"
                  (ngModelChange)="patchIdentity({ birthDate: $event })"
                />
                @if (identityProblem().birthDate; as key) {
                  <span class="bo-error" role="alert">{{ i18n.t(key) }}</span>
                }
              </div>
              }
            </div>
          </app-fold>

          <!-- One recessed note for everything the form explains, at a reading measure: who signs
               and where their identity comes from first, then what every line puts on the paper. -->
          <div class="contract-callout" role="note">
            @if (blankSignatory()) {
              <p>{{ i18n.t('rent.sign.blankDraft') }}</p>
            }
            @if (agentSigns()) {
              <p>
                @if (!signatoryName().trim()) {
                  <span class="bo-state bo-state--warn">{{ i18n.t('rent.sign.missing') }}</span>
                }
                {{ i18n.t('rent.sign.hint.agent') }}
              </p>
            } @else if (signatoryHint(); as hint) {
              <p>
                @if (hint.warn && !signatoryName().trim()) {
                  <span class="bo-state bo-state--warn">{{ i18n.t('rent.sign.missing') }}</span>
                }
                {{ i18n.t(hint.key, { name: representativeName() }) }}
              </p>
            }
            @if (!selfSigns()) {
              @if (recordFields().size > 0) {
                <p>{{ i18n.t('rent.id.fromRecord') }}</p>
              }
              <p>{{ i18n.t('rent.id.draftHint') }}</p>
            }
            <p>{{ i18n.t('rent.sign.frozenHint') }}</p>
            <p>{{ i18n.t('rent.form.hint') }}</p>
            <p>{{ i18n.t('rent.form.hangsOffHint') }}</p>
          </div>

          <!-- Open while it holds the branch to choose or the deposit; a clerk held to his branch
               with no deposit finds only figures already set and optional notes here. -->
          <app-fold
            class="contract-section"
            [heading]="i18n.t('rent.form.details')"
            [open]="!lock.locked() || settings().depositEnabled"
          >
            <div class="contract-grid">
              @if (lock.locked()) {
                <div class="bo-field contract-branch">
                  <p class="bo-fixed-label">{{ i18n.t('branchLock.branch') }}</p>
                  <p class="bo-fixed" id="rf-fixed">{{ lock.ownBranchName(branches()) }}</p>
                  <span class="bo-hint">{{ i18n.t('branchLock.fixedHint') }}</span>
                </div>
              } @else {
                <div class="bo-field contract-branch" [class.is-invalid]="touched.missing('branch', branchId())">
                  <label for="rf-branch">
                    {{ i18n.t('rent.branch') }}<span class="bo-req">{{ i18n.t('common.required') }}</span>
                  </label>
                  <select
                    id="rf-branch"
                    [ngModel]="branchId()"
                    (ngModelChange)="branchId.set($event)"
                    (blur)="touched.touch('branch')"
                  >
                    <option [ngValue]="null">—</option>
                    @for (b of branches(); track b.id) {
                      <option [ngValue]="b.id">{{ branchName(b) }}</option>
                    }
                  </select>
                  @if (touched.missing('branch', branchId())) {
                    <span class="bo-error" role="alert">{{ i18n.t('common.fieldRequired') }}</span>
                  }
                </div>
              }
              <div class="bo-field contract-date">
                <label for="rf-signed">{{ i18n.t('rent.signedOn') }}</label>
                <app-date-input id="rf-signed" [ngModel]="signedOn()" (ngModelChange)="signedOn.set($event)" />
              </div>
              <!-- Only while the switch is on: no field, no zero (§5.6). -->
              @if (settings().depositEnabled) {
                <div class="bo-field contract-deposit" [class.is-invalid]="depositInvalid()">
                  <label for="rf-deposit">
                    {{ i18n.t('rent.deposit') }}<span class="bo-req is-optional">{{ i18n.t('common.optional') }}</span>
                  </label>
                  <input
                    id="rf-deposit"
                    type="number"
                    min="0"
                    class="num mono"
                    [step]="regional.moneyStep()"
                    [ngModel]="deposit()"
                    (ngModelChange)="deposit.set(+$event || 0)"
                  />
                  <p class="bo-hint">{{ i18n.t('rent.form.depositHint') }}</p>
                  @if (depositInvalid()) {
                    <span class="bo-error" role="alert">{{ i18n.t('rent.form.depositNegative') }}</span>
                  }
                </div>
              }
              <!-- Which terms this contract prints (owner, PinSay #302): offered only when the company
                   keeps named wordings beside the standard one, set under Company settings. -->
              @if (wordings().length) {
                <div class="bo-field contract-wording">
                  <label for="rf-wording">{{ i18n.t('rent.form.wording') }}</label>
                  <select id="rf-wording" [ngModel]="wordingKey()" (ngModelChange)="wordingKey.set($event)">
                    <option value="">{{ i18n.t('rent.form.wordingStandard') }}</option>
                    @for (w of wordings(); track w.key) {
                      <option [value]="w.key">{{ i18n.text(w.names) || w.key }}</option>
                    }
                  </select>
                  <p class="bo-hint">{{ i18n.t('rent.form.wordingHint') }}</p>
                </div>
              }
              <!-- VAT is optional when the bill is made, rental bills included (owner, 2026-11-02). The lines
                   freeze the hire rate or nothing, so it is chosen here and fixed at the first issue note. Making
                   it untaxed needs the untaxed-invoice permission; taxing it again does not. A tick box, as at the
                   till (owner, 2026-11-05: "optional when creating the contract, can be checked or not, like selling"):
                   ticked is with VAT. -->
              <div class="bo-field contract-tax">
                <p class="bo-fixed-label">{{ i18n.t('rent.form.taxMode') }}</p>
                <div class="bo-check">
                  <input
                    id="rf-tax"
                    type="checkbox"
                    aria-describedby="rf-tax-hint"
                    [checked]="taxMode() === 'Taxed'"
                    (change)="taxMode.set($any($event.target).checked ? 'Taxed' : 'Untaxed')"
                  />
                  <label for="rf-tax">{{ i18n.t('inv.taxMode.Taxed') }}</label>
                </div>
                <p class="bo-hint" id="rf-tax-hint">{{ taxHint() }}</p>
              </div>
              <div class="bo-field contract-notes contract-wide">
                <label for="rf-notes">
                  {{ i18n.t('rent.notes') }}<span class="bo-req is-optional">{{ i18n.t('common.optional') }}</span>
                </label>
                <input id="rf-notes" maxlength="500" [ngModel]="notes()" (ngModelChange)="notes.set($event)" />
              </div>
            </div>
          </app-fold>

          <!-- The truck is not on the contract (owner, 2026-10-02, answer 12): each issue and
               return note names its own. Values on older contracts are kept and sent back. -->

          <section class="contract-section contract-items rental-items" aria-labelledby="rf-items-title">
            <div class="items-heading">
              <h3 class="contract-section__title" id="rf-items-title">{{ ui('items') }}</h3>
              <button type="button" class="btn-primary" (click)="openPicker()">+ {{ ui('add') }}</button>
            </div>
            <div class="rental-add-panel" [class.is-adding]="addingItems()">
              <label class="scan-label" for="rf-scan">{{ ui('search') }}</label>
              <app-product-picker #entryPicker inputId="rf-scan" [products]="rentable()" [refresh]="refresh"
                [placeholder]="ui('search')" clearOnPick [catchScans]="editingKey() === null" [note]="itemNote"
                (picked)="take($event)" (pickedFromList)="focusQty($event)" (unknownScan)="missed($event)" />
              @if (addingItems()) { <button type="button" class="btn-ghost" (click)="finishPicking()">{{ ui('done') }}</button> }
            </div>
            <p class="scan-feedback" role="status" aria-live="polite">{{ scanNote() || addedNote() }}</p>
            <div class="rental-table" role="table" [attr.aria-label]="ui('items')">
              <div class="rental-table-head" role="row">
                <span role="columnheader">{{ i18n.t('rent.item') }}</span>
                <span role="columnheader">{{ i18n.t('register.qty') }}</span>
                <span role="columnheader">{{ i18n.t('rent.durationDays') }}</span>
                <span role="columnheader">{{ i18n.t('rent.hirePrice') }}</span>
                <span role="columnheader">{{ i18n.t('rent.form.fullPeriod') }}</span>
                <span role="columnheader">{{ ui('actions') }}</span>
              </div>
              @for (line of lines(); track line.key) {
                <div class="rental-row" role="row" [class.row-invalid]="attempts() && lineInvalid(line)">
                  <div class="rental-item-name" role="cell">
                    <strong>{{ lineName(line) }}</strong><small>{{ skuOf(line) }}</small>
                    @if (line.parentKey !== null) { <small>{{ ui('linked') }} {{ parentName(line) }}</small> }
                    @if (attempts() && lineInvalid(line)) { <button type="button" class="line-error-link" (click)="editLine(line.key, true)">{{ ui('review') }}</button> }
                  </div>
                  <!-- Quantity, days and the daily price are typed in the row (owner, 2026-10-06):
                       the editor is for the rest — damage, overdue and what a line hangs off. -->
                  <div role="cell" class="rental-cell" [class.is-invalid]="qtyInvalid(line)">
                    <label class="mobile-label" [attr.for]="'rf-rq' + line.key">{{ i18n.t('register.qty') }}</label>
                    <input
                      [attr.id]="'rf-rq' + line.key"
                      type="number"
                      min="0"
                      class="num mono rental-input"
                      [attr.aria-label]="i18n.t('register.qty') + ' — ' + lineName(line)"
                      [ngModel]="line.qty"
                      (ngModelChange)="patch(line.key, { qty: +$event })"
                      (blur)="touched.touch('q' + line.key)"
                    />
                  </div>
                  <div role="cell" class="rental-cell" [class.is-invalid]="durationInvalid(line)">
                    <label class="mobile-label" [attr.for]="'rf-rd' + line.key">{{ i18n.t('rent.durationDays') }}</label>
                    <input
                      [attr.id]="'rf-rd' + line.key"
                      type="number"
                      min="1"
                      class="num mono rental-input"
                      [attr.aria-label]="i18n.t('rent.durationDays') + ' — ' + lineName(line)"
                      [disabled]="line.parentKey !== null"
                      [ngModel]="durationOf(line)"
                      (ngModelChange)="patch(line.key, { durationDays: +$event })"
                      (blur)="touched.touch('d' + line.key)"
                    />
                  </div>
                  <div role="cell" class="rental-cell">
                    <label class="mobile-label" [attr.for]="'rf-rr' + line.key">{{ i18n.t('rent.hirePrice') }}</label>
                    <input
                      [attr.id]="'rf-rr' + line.key"
                      type="number"
                      min="0"
                      class="num mono rental-input"
                      [step]="regional.moneyStep()"
                      [attr.aria-label]="i18n.t('rent.hirePrice') + ' — ' + lineName(line)"
                      [disabled]="line.parentKey !== null"
                      [ngModel]="line.rentUnitPrice"
                      (ngModelChange)="patch(line.key, { rentUnitPrice: +$event })"
                    />
                    <!-- Allowed, but said: an item with no configured price arrives at 0. -->
                    @if (line.productId && line.parentKey === null && !(line.rentUnitPrice > 0)) {
                      <small class="rental-warn">{{ ui('noPrice') }}</small>
                    }
                  </div>
                  <div role="cell" class="rental-cell rental-cell-total"><span class="mobile-label">{{ i18n.t('rent.form.fullPeriod') }}</span><strong>{{ i18n.money(linePeriod(line)) }}</strong></div>
                  <div role="cell" class="rental-row-actions">
                    <button type="button" class="btn-ghost bo-small" [attr.id]="'rf-edit' + line.key" (click)="editLine(line.key)">{{ ui('edit') }}</button>
                    <details class="row-menu"><summary [attr.aria-label]="ui('actions')">⋯</summary><div>
                      <button type="button" class="btn-ghost" (click)="editLine(line.key)">{{ ui('edit') }}</button>
                      @if (lines().length > 1) { <button type="button" class="btn-ghost" (click)="removeLine(line.key)">{{ ui('remove') }}</button> }
                    </div></details>
                  </div>
                </div>
              } @empty {
                <div class="rental-empty" role="row"><span role="cell">{{ ui('empty') }}</span></div>
              }
            </div>
            @if (attempts() && !lines().length) {
              <p class="bo-error" role="alert">{{ ui('needOne') }}</p>
            }
            <div class="rental-summary"><span>{{ ui('count') }}: <strong>{{ itemCount() }}</strong></span><span>{{ ui('units') }}: <strong>{{ totalUnits() }}</strong></span><span>{{ i18n.t('rent.form.contractValue') }}: <strong>{{ i18n.money(total()) }}</strong></span></div>
          </section>
          <dialog #lineDialog class="rental-editor" aria-labelledby="rf-editor-title" (cancel)="closeEditor()" (close)="editingKey.set(null)">
          @if (editingLine(); as line) {
              <div class="bo-group rental-line">
                <h3 class="bo-group__title" id="rf-editor-title">
                  {{ ui('editor') }} · {{ lineName(line) }}
                </h3>
                @if (line.parentKey !== null) {
                  <p class="bo-group__note">{{ i18n.t('rent.form.accessoryRule') }}</p>
                }
                <div class="bo-fields line-fields">
                  <div class="bo-field line-product" [class.is-invalid]="touched.missing('p' + line.key, line.productId)">
                    <label [attr.for]="'rf-p' + line.key">
                      {{ i18n.t('rent.item') }}<span class="bo-req">{{ i18n.t('common.required') }}</span>
                    </label>
                    <app-product-picker
                      [inputId]="'rf-p' + line.key"
                      [products]="rentable()"
                      [refresh]="refresh"
                      [productId]="line.productId"
                      [invalid]="touched.missing('p' + line.key, line.productId)"
                      (picked)="selectProduct(line.key, $event); touched.touch('p' + line.key)"
                    />
                    @if (touched.missing('p' + line.key, line.productId)) {
                      <span class="bo-error" role="alert">{{ i18n.t('common.fieldRequired') }}</span>
                    }
                  </div>

                  <div class="bo-field line-parent">
                    <label [attr.for]="'rf-parent' + line.key">{{ i18n.t('rent.form.hangsOff') }}</label>
                    <select
                      [attr.id]="'rf-parent' + line.key"
                      [ngModel]="line.parentKey"
                      (ngModelChange)="setParent(line.key, $event)"
                    >
                      <option [ngValue]="null">{{ i18n.t('rent.form.ownLine') }}</option>
                      @for (parent of parentChoices(line.key); track parent.key) {
                        <option [ngValue]="parent.key">{{ parent.label }}</option>
                      }
                    </select>
                  </div>

                  <div class="bo-field line-qty" [class.is-invalid]="qtyInvalid(line)">
                    <label [attr.for]="'rf-q' + line.key">
                      {{ i18n.t('register.qty') }}<span class="bo-req">{{ i18n.t('common.required') }}</span>
                    </label>
                    <input
                      [attr.id]="'rf-q' + line.key"
                      type="number"
                      min="0"
                      class="num mono"
                      [ngModel]="line.qty"
                      (ngModelChange)="patch(line.key, { qty: +$event })"
                      (blur)="touched.touch('q' + line.key)"
                    />
                    @if (qtyInvalid(line)) {
                      <span class="bo-error" role="alert">{{ i18n.t('rent.form.qtyRequired') }}</span>
                    }
                  </div>

                  <div class="bo-field line-duration" [class.is-invalid]="durationInvalid(line)">
                    <label [attr.for]="'rf-d' + line.key">{{ i18n.t('rent.durationDays') }}</label>
                    <input
                      [attr.id]="'rf-d' + line.key"
                      type="number"
                      min="1"
                      class="num mono"
                      [disabled]="line.parentKey !== null"
                      [ngModel]="durationOf(line)"
                      (ngModelChange)="patch(line.key, { durationDays: +$event })"
                      (blur)="touched.touch('d' + line.key)"
                    />
                    <p class="bo-hint">
                      {{ i18n.t(line.parentKey === null ? 'rent.form.durationHint' : 'rent.form.inheritedPeriod') }}
                    </p>
                    @if (durationInvalid(line)) {
                      <span class="bo-error" role="alert">{{ i18n.t('rent.form.durationRequired') }}</span>
                    }
                  </div>

                  <div class="bo-field line-rent">
                    <label [attr.for]="'rf-r' + line.key">{{ i18n.t('rent.hirePrice') }}</label>
                    <input
                      [attr.id]="'rf-r' + line.key"
                      type="number"
                      min="0"
                      class="num mono"
                      [step]="regional.moneyStep()"
                      [disabled]="line.parentKey !== null"
                      [ngModel]="line.rentUnitPrice"
                      (ngModelChange)="patch(line.key, { rentUnitPrice: +$event })"
                    />
                    <p class="bo-hint">{{ i18n.t('rent.perUnitDay') }}</p>
                  </div>

                  <div class="bo-field line-damage" [class.is-invalid]="damageInvalid(line)">
                    <label [attr.for]="'rf-dm' + line.key">
                      {{ i18n.t('rent.damagePrice') }}
                      <span class="bo-req" [class.is-optional]="line.parentKey === null">{{ i18n.t(line.parentKey === null ? 'common.optional' : 'common.required') }}</span>
                    </label>
                    <input
                      [attr.id]="'rf-dm' + line.key"
                      type="number"
                      min="0"
                      class="num mono"
                      [step]="regional.moneyStep()"
                      [ngModel]="line.damageUnitPrice"
                      (ngModelChange)="patch(line.key, { damageUnitPrice: +$event })"
                      (blur)="touched.touch('dm' + line.key)"
                    />
                    @if (itemValueOf(line.productId); as value) {
                      <p class="bo-hint">{{ i18n.t('rent.damage.fromItem', { v: i18n.money(value) }) }}</p>
                    }
                    @if (damageInvalid(line)) {
                      <span class="bo-error" role="alert">{{ i18n.t('rent.form.damageRequired') }}</span>
                    }
                  </div>

                  <div class="bo-field line-overdue" [class.is-invalid]="overdueInvalid(line)">
                    <label [attr.for]="'rf-o' + line.key">{{ i18n.t('rent.overduePrice') }}</label>
                    <input
                      [attr.id]="'rf-o' + line.key"
                      type="number"
                      min="0"
                      class="num mono"
                      [step]="regional.moneyStep()"
                      [placeholder]="derivedOverdue(line)"
                      [ngModel]="line.overdueUnitPrice"
                      (ngModelChange)="patch(line.key, { overdueUnitPrice: $event === null || $event === '' ? null : +$event })"
                    />
                    <p class="bo-hint">
                      {{ i18n.t('rent.form.overdueHint', { m: multiplierOf(line), v: i18n.money(derivedOverdue(line)) }) }}
                      @if (hasOwnMultiplier(line)) {
                        {{ i18n.t('rent.form.itemMultiplier', { m: multiplierOf(line) }) }}
                      }
                      @if (line.parentKey === null) {
                        {{ i18n.t('rent.form.overdueFloor', { v: i18n.money(derivedOverdue(line)) }) }}
                      }
                    </p>
                    @if (overdueInvalid(line)) {
                      <span class="bo-error" role="alert">
                        {{ i18n.t('rent.form.overdueBelowFloor', { v: i18n.money(derivedOverdue(line)) }) }}
                      </span>
                    }
                  </div>
                </div>

                <p class="bo-meta">
                  <span>{{ i18n.t('rent.form.fullPeriod') }}</span>
                  <strong class="mono">{{ i18n.money(linePeriod(line)) }}</strong>
                  @if (lines().length > 1) {
                    <button type="button" class="btn-ghost bo-small" (click)="removeLine(line.key)">
                      {{ i18n.t('rent.form.removeLine') }}
                    </button>
                  }
                </p>
                <div class="editor-foot">
                  <button type="button" class="btn-primary editor-close" (click)="closeEditor()">{{ ui('done') }}</button>
                </div>
              </div>
            }
          </dialog>
        </div>

        <!-- One bar, always on screen: the contract value and Cancel / Save. -->
        <div class="ui-actionbar ui-actionbar--sticky contract-bar">
          <p class="total">
            <span>{{ i18n.t('rent.form.contractValue') }}</span>
            <strong class="mono">{{ i18n.money(total()) }}</strong>
          </p>
          <button type="button" class="btn-ghost" (click)="cancelled.emit()">{{ i18n.t('common.cancel') }}</button>
          <button type="button" class="btn-primary" [disabled]="busy()" (click)="save()">
            {{ i18n.t(contractId() ? 'common.save' : 'rent.form.raise') }}
          </button>
        </div>
      </section>
    </div>
  `,
  styleUrls: ['../backoffice-ui.scss', '../../../ui/ui-patterns.scss', './rental-builder.scss'],
  styles: [
    `
      /* The page holds one width and sits in the middle of a wide workspace (owner, 2026-10-06). */
      .contract-form {
        inline-size: 100%;
        max-inline-size: 80rem;
        margin-inline: auto;
      }

      /* --- sections: a title, then at most two fields to a row ----------------------------- */
      .contract-body {
        padding: var(--sp-6);
        gap: var(--sp-5);
      }
      /* The blank-signatory warning keeps the form's own inset, just above the action bar. */
      .contract-blank {
        padding: 0 var(--sp-6) var(--sp-5);
      }
      .contract-section {
        display: grid;
        gap: var(--sp-4);
        align-content: start;
        min-inline-size: 0;
      }
      /* A hairline divides the sections, never a nested card. */
      .contract-section:not(:first-child) {
        border-block-start: 1px solid var(--border);
        padding-block-start: var(--sp-5);
      }
      .contract-section__title {
        margin: 0;
        font-size: var(--fs-card);
        font-weight: 600;
        color: var(--text);
      }
      .contract-grid {
        display: grid;
        grid-template-columns: repeat(2, minmax(0, 1fr));
        gap: var(--sp-5) var(--sp-6);
        /* Feedback grows under its own control; the neighbour in the row stays where it is. */
        align-items: start;
      }
      .contract-wide {
        grid-column: 1 / -1;
      }
      /* Who signs a person's contract: two radios on one line, the question before them. The
         choice is said by the checked radio and its label, never by colour. */
      .signs-choice {
        display: flex;
        flex-wrap: wrap;
        align-items: center;
        gap: var(--sp-2) var(--sp-5);
      }
      .signs-choice__label {
        font-size: var(--fs-sm);
        font-weight: 600;
        color: var(--text);
      }

      /* --- a field: label straight above its control, the same height everywhere ---------- */
      .contract-form .contract-grid .bo-field > label,
      .contract-form .contract-grid .bo-fixed-label {
        margin-block-end: var(--sp-2);
      }
      .contract-grid :is(input, select) {
        min-block-size: var(--control-h);
      }
      .contract-grid .bo-fixed {
        min-block-size: var(--control-h);
        display: flex;
        align-items: center;
      }
      /* Required and optional are both said in words beside the label: required in the text
         colour and weight 600, optional in the muted colour at the label's own weight. */
      .contract-form .bo-req {
        font-size: var(--fs-meta);
        font-weight: 600;
        letter-spacing: 0;
        text-transform: none;
        color: var(--text);
      }
      .contract-form .bo-req.is-optional {
        font-weight: 400;
        color: var(--muted);
      }

      /* A select and its secondary action on one row; the action drops under it on a phone. */
      .contract-pick {
        display: flex;
        align-items: center;
        gap: var(--sp-3);
      }
      .contract-pick > select {
        flex: 1 1 auto;
        min-inline-size: 0;
      }
      .contract-pick > .btn-ghost {
        flex: none;
        min-block-size: var(--control-h-sm);
        white-space: nowrap;
      }
      /* The select is no longer the field's direct child, so the shared red edge is restated. */
      .bo-field.is-invalid .contract-pick > select {
        border-color: var(--danger);
      }

      .contract-new-site {
        display: grid;
        gap: var(--sp-4);
        padding: var(--sp-5);
        border-radius: var(--radius);
        background: var(--surface-2);
        border: 1px solid var(--border);
      }
      .contract-new-site__foot {
        display: flex;
        flex-wrap: wrap;
        align-items: center;
        justify-content: space-between;
        gap: var(--sp-3) var(--sp-5);
      }
      .contract-new-site__foot .bo-hint {
        margin: 0;
        flex: 1 1 18rem;
      }

      /* --- the one note: recessed, neutral, at a reading measure --------------------------- */
      .contract-callout {
        display: grid;
        gap: var(--sp-2);
        max-inline-size: var(--read-max);
        padding: var(--sp-4) var(--sp-5);
        border-radius: var(--radius);
        /* A recessed note, not a selection: surface-tint is a selected row's ground. */
        background: var(--surface-2);
        border: 1px solid var(--border);
        font-size: var(--fs-sm);
        color: var(--muted);
      }
      .contract-callout p {
        margin: 0;
      }
      .contract-callout .bo-state {
        margin-inline-end: var(--sp-2);
      }

      /* --- items ---------------------------------------------------------------------------- */
      .contract-picker {
        display: grid;
        gap: var(--sp-2);
      }
      .line-fields {
        grid-template-columns: repeat(12, minmax(0, 1fr));
        align-items: start;
      }
      .rental-line {
        padding: var(--sp-5);
        border: 1px solid var(--border);
        border-radius: var(--radius-lg);
        background: var(--surface);
      }
      /* The lines are one list inside the items section, not groups divided by a rule. */
      .contract-items .rental-line + .rental-line {
        padding-block-start: var(--sp-5);
      }
      .line-product { grid-column: 1 / 9; }
      .line-parent { grid-column: 9 / 13; }
      .line-qty { grid-column: 1 / 3; }
      .line-duration { grid-column: 3 / 6; }
      .line-rent { grid-column: 6 / 9; }
      .line-damage { grid-column: 9 / 13; }
      .line-overdue { grid-column: 1 / -1; }
      .rental-line .bo-meta {
        align-items: center;
        padding: 0.55rem 0.7rem;
        border-radius: var(--radius);
        background: var(--surface);
      }

      /* The action bar sticks to the foot of the screen while the form scrolls; a sticky child
         needs its panel not to clip it. */
      .form-panel {
        overflow: visible;
      }
      .contract-bar {
        align-items: center;
      }
      /* In the bar: Add line at the start, the value pushing Cancel and Save to the end. */
      .total {
        margin: 0;
        margin-inline-start: auto;
        display: flex;
        flex-wrap: wrap;
        align-items: baseline;
        gap: 0.5rem;
        font-size: 0.86rem;
        color: var(--bo-ink-2);
      }
      .total strong {
        font-size: 1.05rem;
        font-weight: 700;
        color: var(--text);
        font-variant-numeric: tabular-nums;
      }

      @media (max-width: 56rem) {
        .line-product,
        .line-parent,
        .line-overdue { grid-column: 1 / -1; }
        .line-qty { grid-column: 1 / 4; }
        .line-duration { grid-column: 4 / 7; }
        .line-rent { grid-column: 7 / 10; }
        .line-damage { grid-column: 10 / 13; }
      }

      /* One column on a phone. Pinning each field to column 1 of the twelve, as this once did,
         left every field a twelfth of the width: a few letters, the customer button four lines tall. */
      @media (max-width: 42rem) {
        .contract-body {
          padding: var(--sp-5);
        }
        .contract-blank {
          padding: 0 var(--sp-5) var(--sp-5);
        }
        .contract-grid,
        .line-fields { grid-template-columns: minmax(0, 1fr); }
        .line-fields > * { grid-column: 1 / -1; }
        .contract-pick {
          flex-direction: column;
          align-items: stretch;
        }
        .contract-pick > .btn-ghost {
          align-self: start;
        }
        .contract-new-site { padding: var(--sp-4); }
        .rental-line { padding: var(--sp-4); }
      }
    `,
  ],
})
export class RentalContractFormComponent implements OnInit {
  private readonly api = inject(RentalsApi);
  private readonly admin = inject(AdminService);
  private readonly confirm = inject(ConfirmService);
  readonly i18n = inject(I18nService);
  readonly lock = inject(BranchLock);
  readonly regional = inject(RegionalService);

  readonly contractId = input<string | null>(null);
  readonly customers = input<AdminCustomer[]>([]);
  /** A customer created from this form (FR-1), for the section to add to its own list. */
  readonly customerCreatedOut = output<AdminCustomer>({ alias: 'customerCreated' });

  private readonly auth = inject(AuthService);
  private readonly connectivity = inject(ConnectivityService);
  readonly mayCreateCustomer = computed(() => canCreateCustomer(this.auth.currentUser(), this.connectivity.online()));
  readonly quickCustomer = signal(false);
  /**
   * Customers created here, until the section's list carries them: the form picks the new one at
   * once, with its signatory prefill, rather than waiting for its input to catch up.
   */
  private readonly created = signal<AdminCustomer[]>([]);
  readonly allCustomers = computed(() => {
    const listed = this.customers();
    return [...listed, ...this.created().filter((c) => !listed.some((l) => l.id === c.id))];
  });

  /** The new customer becomes the contract's; nothing else typed on the form is touched. */
  async customerCreated(customer: AdminCustomer): Promise<void> {
    this.quickCustomer.set(false);
    this.created.update((list) => [...list, customer]);
    this.customerCreatedOut.emit(customer);
    await this.pickCustomer(customer.id);
  }
  readonly branches = input<AdminBranch[]>([]);
  /**
   * The catalogue, read from the session's shared copy rather than passed in. A scan that
   * misses asks that copy to refresh and needs the refreshed list in the same breath; an
   * input would still hold the old one until the next change detection.
   */
  private readonly catalog = inject(CatalogStore);
  readonly products = this.catalog.active;
  readonly settings = input.required<RentalSettings>();

  readonly saved = output<string>();
  readonly cancelled = output<void>();

  readonly touched = new TouchedFields();
  readonly busy = signal(false);
  readonly error = signal<string | null>(null);

  readonly customerId = signal<string | null>(null);
  readonly signatoryName = signal('');
  /** The rest of the signatory's identity, as it will travel: blank strings, never null. */
  readonly identity = signal<Omit<SignatoryPrefill, 'name'>>({
    idNumber: '',
    idType: '',
    idIssuer: '',
    idIssuedOn: '',
    birthDate: '',
  });
  /** The fields the chosen customer's (or representative's) record already holds — shown, not typed over. */
  readonly recordFields = signal<Set<keyof SignatoryPrefill>>(new Set());
  readonly idTypes = ID_DOCUMENT_TYPES;
  /** A company's representatives, current first — the only names that may sign for it. */
  readonly representatives = signal<IdentifiedRepresentative[]>([]);
  readonly representativeId = signal<string | null>(null);

  readonly sites = signal<CustomerSite[]>([]);
  readonly siteId = signal<string | null>(null);
  /** The site stored on the draft being edited, which stays a choice even once it is closed. */
  private readonly storedSiteId = signal<string | null>(null);
  /** False only on a draft raised before the site rule with no site yet; a new contract always needs one. */
  readonly siteRequired = signal(true);
  readonly addingSite = signal(false);
  readonly newSiteName = signal('');
  readonly newSiteAddress = signal('');
  readonly newSitePhone = signal('');

  readonly truckType = signal('');
  readonly truckNumber = signal('');
  readonly truckOwnerPhone = signal('');
  /**
   * The company's named contract wordings (PinSay #302), read from the printed-documents settings
   * whoever prints already reads. Empty — no select — when there are none or they cannot be read.
   */
  readonly wordings = signal<ContractWording[]>([]);
  /** The chosen wording's key; '' is the standard one. */
  readonly wordingKey = signal('');

  /**
   * Whether this contract's bills carry VAT. A new contract starts without: the seller ticks it when the customer
   * asks for a tax invoice, and every seller decides (owner, 2026-11-05).
   */
  readonly taxMode = signal<TaxMode>('Untaxed');
  readonly taxHint = computed(() => {
    if (this.taxMode() === 'Untaxed') return this.i18n.t('rent.form.taxHint.untaxed');
    const rate = this.settings().taxRate;
    return rate > 0
      ? this.i18n.t('rent.form.taxHint.rate', { rate: this.rateWords(rate) })
      : this.i18n.t('rent.form.taxHint.noRate');
  });
  /** The figure, and the profile's name for it when it has one: "19 % (Taux normal)". */
  private rateWords(rate: number): string {
    const name = this.i18n.rateLabel(rate);
    return name ? `${rate} % (${name})` : `${rate} %`;
  }
  private readonly documents = inject(StoreDocumentsApi);
  /**
   * Whether this contract is held to the signatory rule. A new one always is; an existing one
   * says so itself, and a draft raised before the rule is not nagged about a blank it may keep.
   */
  private readonly signatoryRequired = signal(true);
  readonly branchId = signal<string | null>(null);
  readonly signedOn = signal<string>(today(this.regional.settings().timeZone));
  readonly notes = signal('');
  /** The deposit signed on the paper. Read and sent only while the switch is on. */
  readonly deposit = signal(0);
  // A new contract opens with no line (owner, 2026-10-06): an empty "no item" row only had to be
  // replaced or deleted. The first scan or search adds the first line.
  readonly lines = signal<DraftLine[]>([]);

  @ViewChild('lineDialog') private lineDialog?: ElementRef<HTMLDialogElement>;
  @ViewChild('entryPicker') private entryPicker?: ProductPickerComponent;
  readonly editingKey = signal<number | null>(null);
  readonly editingLine = computed(() => this.lines().find(l => l.key === this.editingKey()) ?? null);
  readonly addingItems = signal(false);
  readonly addedNote = signal('');
  readonly itemCount = computed(() => this.lines().filter(l => l.productId).length);
  readonly totalUnits = computed(() => this.lines().filter(l => l.productId).reduce((n, l) => n + l.qty, 0));
  readonly invalidLines = computed(() => this.lines().filter(l => this.lineInvalid(l)));
  ui(key: BuilderCopyKey): string { return builderCopy(this.i18n.lang(), key); }
  lineName(line: DraftLine): string {
    const product = this.products().find(p => p.id === line.productId);
    return product ? this.productName(product) : this.i18n.t('rent.form.noItem');
  }
  skuOf(line: DraftLine): string { return this.products().find(p => p.id === line.productId)?.sku ?? ''; }
  parentName(line: DraftLine): string {
    const parent = this.lines().find(l => l.key === line.parentKey);
    return parent ? this.lineName(parent) : '';
  }
  lineInvalid(line: DraftLine): boolean {
    return !line.productId || !(line.qty > 0) ||
      (line.parentKey === null && !(line.durationDays >= 1)) ||
      (line.parentKey !== null && !(line.damageUnitPrice > 0)) || this.overdueInvalid(line);
  }
  openPicker(): void {
    this.addingItems.set(true);
    this.entryPicker?.clear();
  }
  finishPicking(): void {
    this.addingItems.set(false);
    this.entryPicker?.open.set(false);
  }
  editLine(key: number, focusError = false): void {
    document.querySelectorAll<HTMLDetailsElement>('.row-menu[open]').forEach(menu => menu.open = false);
    this.editingKey.set(key);
    this.lineDialog?.nativeElement.showModal();
    setTimeout(() => {
      const dialog = this.lineDialog?.nativeElement;
      const control = focusError
        ? dialog?.querySelector<HTMLElement>('.is-invalid input, .is-invalid select')
        : dialog?.querySelector<HTMLElement>(this.editingLine()?.productId ? '#rf-q' + key : 'input');
      control?.focus();
    });
  }
  closeEditor(): void {
    const key = this.editingKey();
    this.lineDialog?.nativeElement.close();
    this.editingKey.set(null);
    if (key !== null) document.getElementById('rf-edit' + key)?.focus();
  }


  /**
   * The items carrying their own overdue multiplier, by product id, read from the server so the
   * figure previewed here is the figure the line will be frozen at — not the company's for an
   * item that has its own. Empty until it lands, and the company figure stands in meanwhile.
   */
  private readonly overrides = signal<ReadonlyMap<string, number>>(new Map());
  /** Predefined ordinary hire prices, kept separate from sale prices and asset values. */
  private readonly rentalPrices = signal<ReadonlyMap<string, number>>(new Map());
  /** Each item's fixed replacement value (answer 13), which a line's damage price starts from. */
  private readonly damageValues = signal<ReadonlyMap<string, number>>(new Map());

  private nextKey = 2;

  /** Said under the scan box when a code read nothing, since nothing else would show it. */
  readonly scanNote = signal<string | null>(null);

  /**
   * Whether the catalogue distinguishes hire articles from the rest yet.
   *
   * <p>The flag is new, so every existing article carries false. Filtering on it regardless
   * would empty this screen for a company that has been raising contracts all along — the
   * screen would look broken, and the cause would be invisible. So it takes effect the moment
   * anybody marks a first article, and until then a contract may name anything.</p>
   */
  /** Every item may go on a contract: each one is rented and sold (owner, 2026-10-06). */
  readonly rentable = computed(() => this.products());

  /** What a picker calls when a scan matches nothing here: refresh, then the list it may pick from. */
  readonly refresh = async (): Promise<readonly SearchableProduct[]> => {
    await this.catalog.refresh();
    return this.rentable();
  };

  readonly total = computed(() => this.lines().reduce((sum, line) => sum + this.linePeriod(line), 0));

  readonly customer = computed(() => this.allCustomers().find((c) => c.id === this.customerId()) as IdentifiedCustomer | undefined);

  readonly mode = computed(() => signatoryMode(this.customer()));

  /** A person customer's agent signs instead of the customer (owner, 2026-10-06). Read only for a person. */
  readonly byAgent = signal(false);
  readonly agentSigns = computed(() => this.mode() === 'person' && this.byAgent());
  /** The person signs himself: his identity is his record's, not asked on the contract. */
  readonly selfSigns = computed(() => this.mode() === 'person' && !this.byAgent());

  /**
   * Who signs for a person. The agent starts blank — nothing of the customer's record is his — and
   * going back to the customer puts the customer's name and paper back.
   */
  chooseSigner(agent: boolean): void {
    this.byAgent.set(agent);
    if (agent) {
      this.signatoryName.set('');
      this.identity.set({ idNumber: '', idType: '', idIssuer: '', idIssuedOn: '', birthDate: '' });
      this.recordFields.set(new Set());
    } else {
      this.applyPrefill(signatoryPrefill(this.customer()));
    }
  }

  /** Open sites, plus the one this draft already names if it has been closed since. */
  readonly siteChoices = computed(() => this.sites().filter((s) => s.isActive || s.id === this.storedSiteId()));

  /** A method, not a computed: `TouchedFields` is a plain set, so a computed would never see a touch. */
  siteInvalid(): boolean {
    return this.siteRequired() && this.touched.missing('site', this.siteId() ?? '');
  }

  /** Which sentence sits under the signatory fields — and whether it explains a blank. */
  readonly signatoryHint = computed(() => signatoryHintKey(this.customer()));

  /** The company hint names its representative; the other hints ignore the parameter. */
  readonly representativeName = computed(() => this.customer()?.representative?.fullName ?? '');

  locked(field: keyof SignatoryPrefill): boolean {
    return this.recordFields().has(field);
  }

  patchIdentity(change: Partial<Omit<SignatoryPrefill, 'name'>>): void {
    this.identity.update((current) => ({ ...current, ...change }));
  }

  /** Said above the foot when saving would leave a draft the first issue note will refuse. */
  readonly blankSignatory = computed(() => this.signatoryRequired() && !this.signatoryName().trim());

  async ngOnInit(): Promise<void> {
    void this.loadWordings();
    await this.loadOverrides();
    const id = this.contractId();
    if (!id) {
      this.branchId.set(this.lock.defaultBranch(this.branches()));
      return;
    }
    this.busy.set(true);
    try {
      const contract = await this.api.getContract(id);
      this.customerId.set(contract.customerId);
      this.deposit.set(contract.depositAmount ?? 0);
      // The stored snapshot as it is, not a fresh prefill: the representative may have changed
      // since, and what is on this paper stays until somebody changes the customer.
      this.signatoryName.set(contract.signatoryName ?? '');
      this.identity.set({
        idNumber: contract.signatoryIdNumber ?? '',
        idType: contract.signatoryIdType ?? '',
        idIssuer: contract.signatoryIdIssuer ?? '',
        idIssuedOn: contract.signatoryIdIssuedOn ?? '',
        birthDate: contract.signatoryBirthDate ?? '',
      });
      this.signatoryRequired.set(contract.signatoryRequired);
      this.byAgent.set(!!contract.signedByAgent);
      this.siteId.set(contract.siteId ?? null);
      this.storedSiteId.set(contract.siteId ?? null);
      this.siteRequired.set(!!contract.siteId);
      this.truckType.set(contract.truckType ?? '');
      this.truckNumber.set(contract.truckNumber ?? '');
      this.truckOwnerPhone.set(contract.truckOwnerPhone ?? '');
      this.wordingKey.set(contract.wordingKey ?? '');
      this.taxMode.set(contract.taxMode ?? 'Taxed');
      await this.loadCustomerPaper(contract.customerId, contract.signatoryName ?? '');
      // A non-admin's contract is theirs: the server keeps it at their branch whatever is sent.
      this.branchId.set(this.lock.lockedBranchId() ?? contract.branchId);
      this.signedOn.set(contract.signedAtUtc.slice(0, 10));
      this.notes.set(contract.notes ?? '');
      const keys = new Map<string, number>();
      contract.lines.forEach((line) => keys.set(line.id, this.nextKey++));
      this.lines.set(
        contract.lines.map((line) => ({
          key: keys.get(line.id)!,
          productId: line.productId,
          qty: line.qty,
          durationDays: line.durationDays,
          rentUnitPrice: line.rentUnitPrice,
          damageUnitPrice: line.damageUnitPrice,
          overdueUnitPrice: line.overdueUnitPrice,
          parentKey: line.parentLineId ? (keys.get(line.parentLineId) ?? null) : null,
        })),
      );
    } catch (e) {
      this.error.set(adminErrorMessage(e));
    } finally {
      this.busy.set(false);
    }
  }

  /** A failed read offers no choice: the contract keeps the wording it has, the standard one when new. */
  private async loadWordings(): Promise<void> {
    try {
      this.wordings.set((await this.documents.get()).contractWordings ?? []);
    } catch {
      this.wordings.set([]);
    }
  }

  /** A failed read leaves the company figure standing in; the server still applies the item's own. */
  private async loadOverrides(): Promise<void> {
    try {
      const rows = await this.api.listProductMultipliers();
      this.overrides.set(
        new Map(rows.filter((r) => r.overdueMultiplier !== null).map((r) => [r.productId, r.overdueMultiplier!])),
      );
      this.rentalPrices.set(new Map(rows.map((r) => [r.productId, r.rentalUnitPrice])));
      this.damageValues.set(
        new Map(rows.filter((r) => (r.rentalDamageUnitPrice ?? 0) > 0).map((r) => [r.productId, r.rentalDamageUnitPrice!])),
      );
    } catch {
      this.overrides.set(new Map());
      this.rentalPrices.set(new Map());
      this.damageValues.set(new Map());
    }
  }

  /**
   * A customer's sites, and for a company the representatives that may sign for it. Read when the
   * customer is chosen; a failed read leaves the lists empty and says why, and never picks for the clerk.
   */
  private async loadCustomerPaper(customerId: string | null, keepName: string): Promise<void> {
    this.sites.set([]);
    this.representatives.set([]);
    this.representativeId.set(null);
    if (!customerId) return;
    const customer = this.allCustomers().find((c) => c.id === customerId);
    try {
      const [sites, history] = await Promise.all([
        this.api.listSites(customerId),
        customer?.kind === 'Company' ? this.admin.listRepresentatives(customerId) : Promise.resolve([]),
      ]);
      this.sites.set(sites);
      const choices = representativeChoices(history as IdentifiedRepresentative[]);
      this.representatives.set(choices);
      const name = keepName.trim().toLocaleLowerCase();
      const chosen = name
        ? choices.find((r) => r.fullName.trim().toLocaleLowerCase() === name)
        : choices.find((r) => !r.toUtc);
      this.representativeId.set(chosen?.id ?? null);
      // An agent's paper is his own: nothing of the customer's record locks it.
      this.recordFields.set(
        this.agentSigns() ? new Set() : fromRecord(this.mode(), signatoryPrefill(this.customer(), chosen ?? null)),
      );
      // A new contract with one open site takes it; the clerk still sees it and may change it.
      const open = sites.filter((s) => s.isActive);
      if (!this.siteId() && open.length === 1 && !this.contractId()) this.siteId.set(open[0].id);
    } catch (e) {
      this.error.set(adminErrorMessage(e));
    }
  }

  // ---------- the draft ----------

  /**
   * A customer chosen by hand. The signatory and the site are replaced, not merged: whatever was
   * on the form belonged to the previous customer, and his site or his representative carried
   * over to another customer would end up on the wrong paper.
   */
  async pickCustomer(id: string | null): Promise<void> {
    this.customerId.set(id);
    this.siteId.set(null);
    this.storedSiteId.set(null);
    this.siteRequired.set(true);
    this.addingSite.set(false);
    // Another customer starts with himself signing; an agent named for the previous one is not his.
    this.byAgent.set(false);
    this.applyPrefill(signatoryPrefill(this.allCustomers().find((c) => c.id === id) as IdentifiedCustomer | undefined));
    await this.loadCustomerPaper(id, '');
  }

  /** One of the company's representatives signs; his identity comes with him, the birth date stays the contract's. */
  pickRepresentative(id: string | null): void {
    this.representativeId.set(id);
    const rep = this.representatives().find((r) => r.id === id) ?? null;
    const prefill = rep ? signatoryPrefill(this.customer(), rep) : { ...signatoryPrefill(undefined), birthDate: '' };
    this.applyPrefill({ ...prefill, birthDate: this.identity().birthDate });
    this.recordFields.set(fromRecord(this.mode(), prefill));
  }

  private applyPrefill(prefill: SignatoryPrefill): void {
    this.signatoryName.set(prefill.name);
    this.identity.set({
      idNumber: prefill.idNumber,
      idType: prefill.idType,
      idIssuer: prefill.idIssuer,
      idIssuedOn: prefill.idIssuedOn,
      birthDate: prefill.birthDate,
    });
    this.recordFields.set(fromRecord(this.mode(), prefill));
  }

  /** A new site, added from the contract itself and chosen straight away (answer 9: one customer, several sites). */
  async createSite(): Promise<void> {
    const customerId = this.customerId();
    this.touched.touch('siteName');
    if (!customerId || !this.newSiteName().trim()) return;
    if (!(await this.confirm.ask(saveRequest(this.i18n, this.newSiteName().trim())))) return;
    this.busy.set(true);
    this.error.set(null);
    try {
      const site = await this.api.createSite(customerId, {
        name: this.newSiteName().trim(),
        address: this.newSiteAddress().trim() || null,
        phone: this.newSitePhone().trim() || null,
        isActive: true,
      });
      this.sites.update((sites) => [...sites, site]);
      this.siteId.set(site.id);
      this.addingSite.set(false);
      this.newSiteName.set('');
      this.newSiteAddress.set('');
      this.newSitePhone.set('');
    } catch (e) {
      this.error.set(adminErrorMessage(e));
    } finally {
      this.busy.set(false);
    }
  }

  private blank(key: number): DraftLine {
    return {
      key,
      productId: null,
      qty: 1,
      durationDays: 30,
      rentUnitPrice: 0,
      damageUnitPrice: 0,
      overdueUnitPrice: null,
      parentKey: null,
    };
  }

  addLine(): void {
    this.lines.update((lines) => [...lines, this.blank(this.nextKey++)]);
  }

  /**
   * An article arriving from the scan box.
   *
   * <p>Scanning the same article twice means two of it, not two lines of one — which is what
   * somebody counting boxes onto a lorry expects, and what a till does. A new contract opens
   * with no lines (owner, 2026-10-06); a blank one added by hand is filled rather than followed,
   * so a scan does not leave an empty line hanging above it.</p>
   *
   * <p>An accessory line is passed over when looking for a match: a coupler hung off one
   * scaffold is a different line from the same coupler hung off another, and merging them
   * would take the second one's compensation price with it.</p>
   */
  take(product: SearchableProduct): void {
    this.scanNote.set(null);
    this.addedNote.set(this.ui('added') + ': ' + localizedName(product, this.i18n.lang()));
    // Entry mode remains open for the next selection, after the picker clears this choice.
    if (this.addingItems()) setTimeout(() => { if (this.addingItems()) this.entryPicker?.clear(); });

    const already = this.lines().find((l) => l.productId === product.id && l.parentKey === null);
    if (already) {
      this.patch(already.key, { qty: already.qty + 1 });
      return;
    }

    const blank = this.lines().find((l) => l.productId === null);
    if (blank) {
      this.selectProduct(blank.key, product);
      return;
    }

    this.lines.update((lines) => [
      ...lines,
      {
        ...this.blank(this.nextKey++),
        productId: product.id,
        rentUnitPrice: this.priceOf(product.id),
        damageUnitPrice: damagePriceFor(0, this.itemValueOf(product.id)),
      },
    ]);
  }

  /**
   * After a deliberate choice from the list, the next thing to type is how many: the cursor goes
   * to that line's quantity, selected so the first digit replaces it. A scan never comes here
   * (the picker's `pickedFromList`), so scanning keeps the box focused for the next code.
   */
  focusQty(product: SearchableProduct): void {
    const line = this.lines().find((l) => l.productId === product.id && l.parentKey === null);
    if (!line) return;
    this.addingItems.set(false);
    setTimeout(() => {
      const input = document.getElementById('rf-rq' + line.key) as HTMLInputElement | null;
      input?.focus();
      input?.select();
    });
  }

  /** A code nobody's catalogue knows. Said out loud rather than swallowed. */
  missed(code: string): void {
    this.addedNote.set('');
    this.scanNote.set(this.i18n.t('pick.unknown', { code }));
  }

  /**
   * Asks first only when the line holds something — an article, a figure changed from the
   * starting one. An untouched new line has nothing to lose, so it goes without a question.
   */
  async removeLine(key: number): Promise<void> {
    const line = this.lines().find((l) => l.key === key);
    if (!line) return;
    if (differsFromBlank(line, this.blank(line.key)) && !(await this.confirm.ask(removeLineRequest(this.i18n)))) return;
    if (this.editingKey() === key) this.closeEditor();
    // An accessory whose parent has gone is an accessory of nothing; it becomes its own line.
    this.lines.update((lines) =>
      lines.filter((l) => l.key !== key).map((l) => (l.parentKey === key ? { ...l, parentKey: null } : l)),
    );
  }

  patch(key: number, change: Partial<DraftLine>): void {
    this.lines.update((lines) => lines.map((l) => (l.key === key ? { ...l, ...change } : l)));
  }

  /**
   * A fresh item starts at its configured hire price and its fixed replacement value; an included
   * accessory always stays free. A damage price the clerk typed is kept — only one that was the
   * previous item's own value (or nothing) follows the item.
   */
  selectProduct(key: number, product: SearchableProduct): void {
    const line = this.lines().find((candidate) => candidate.key === key);
    const previousValue = line?.productId ? (this.itemValueOf(line.productId) ?? 0) : 0;
    const typed = line && line.damageUnitPrice !== previousValue ? line.damageUnitPrice : 0;
    this.patch(key, {
      productId: product.id,
      rentUnitPrice: line?.parentKey === null ? this.priceOf(product.id) : 0,
      damageUnitPrice: damagePriceFor(typed, this.itemValueOf(product.id)),
    });
  }

  /** Each item's quantity at the contract's branch, read once per branch for the item search. */
  readonly stockLevels = signal<ReadonlyMap<string, number>>(new Map());
  private readonly loadLevels = effect(() => {
    const branch = this.branchId();
    if (!branch) {
      this.stockLevels.set(new Map());
      return;
    }
    // Shown beside each item, never a reason to refuse one: a missing answer just shows less.
    void Promise.resolve().then(() => this.admin.stockLevels(branch)).then(
      (rows) => {
        if (this.branchId() === branch) this.stockLevels.set(new Map(rows.map((r) => [r.productId, r.stockQty])));
      },
      () => this.stockLevels.set(new Map()),
    );
  });

  /** What the search list says beside an item: its daily price and what the branch has of it. */
  readonly itemNote = (product: SearchableProduct): string | null => {
    const parts: string[] = [];
    const price = this.priceOf(product.id);
    if (price > 0) parts.push(this.ui('notePrice').replace('{v}', this.i18n.money(price)));
    const stock = this.stockLevels().get(product.id);
    if (stock !== undefined) parts.push(this.ui('noteStock').replace('{v}', String(stock)));
    return parts.length ? parts.join(this.ui('noteJoin')) : null;
  };

  private priceOf(productId: string): number {
    return this.rentalPrices().get(productId) ?? 0;
  }

  /**
   * What a lost or destroyed piece of this item costs by default: its sale price (owner,
   * 2026-10-06), else the replacement value set for it under rental settings, else nothing.
   * A price typed on the line always wins over either.
   */
  itemValueOf(productId: string | null): number | null {
    if (!productId) return null;
    const sale = this.products().find((p) => p.id === productId)?.price ?? 0;
    if (sale > 0) return sale;
    return this.damageValues().get(productId) ?? null;
  }

  /** Nobody hires a coupler: an accessory rents at zero, and the server refuses anything else. */
  setParent(key: number, parentKey: number | null): void {
    const line = this.lines().find((candidate) => candidate.key === key);
    this.patch(
      key,
      parentKey === null
        ? { parentKey, rentUnitPrice: line?.productId ? this.priceOf(line.productId) : (line?.rentUnitPrice ?? 0) }
        : { parentKey, rentUnitPrice: 0, overdueUnitPrice: null },
    );
  }

  /** One level only: an accessory of an accessory has no period to inherit. */
  parentChoices(key: number): { key: number; label: string }[] {
    return this.lines()
      .filter((l) => l.key !== key && l.parentKey === null)
      .map((l, i) => ({
        key: l.key,
        label: `${i + 1} — ${this.products().find((p) => p.id === l.productId)?.sku ?? this.i18n.t('rent.form.noItem')}`,
      }));
  }

  /** An accessory inherits its parent's period: a coupler cannot expire before its scaffold. */
  durationOf(line: DraftLine): number {
    if (line.parentKey === null) return line.durationDays;
    return this.lines().find((l) => l.key === line.parentKey)?.durationDays ?? line.durationDays;
  }

  /** The multiplier in force for this line's item: its own when it has one, else the company's. */
  multiplierOf(line: DraftLine): number {
    return multiplierFor(this.settings(), line.productId ? this.overrides().get(line.productId) : null);
  }

  hasOwnMultiplier(line: DraftLine): boolean {
    return !!line.productId && this.overrides().has(line.productId);
  }

  /**
   * What the overdue price comes out at on the multiplier in force — the same arithmetic the
   * server freezes onto the line when the field is left blank, and the **floor** for a figure
   * typed into it: a negotiated overdue price may only go up from here.
   */
  derivedOverdue(line: DraftLine): number {
    return overdueUnitPrice(line.rentUnitPrice, this.multiplierOf(line), this.regional.decimals());
  }

  /** What this line invoices the day it goes out: the whole agreed period, at the signed price. */
  linePeriod(line: DraftLine): number {
    return rentalAmount(line.rentUnitPrice, this.durationOf(line), line.qty, this.regional.decimals());
  }

  // ---------- what the server will refuse ----------

  /**
   * `TouchedFields.missing` asks whether a field has been left and is still empty. A number is
   * "empty" here when it is not a figure the server would accept, so each of these hands it the
   * verdict rather than the value — same timing rule, same one place that decides it.
   */
  qtyInvalid(line: DraftLine): boolean {
    return this.touched.missing('q' + line.key, line.qty > 0 ? 'ok' : '');
  }

  durationInvalid(line: DraftLine): boolean {
    return line.parentKey === null && this.touched.missing('d' + line.key, line.durationDays >= 1 ? 'ok' : '');
  }

  /** Zero compensation on an accessory is the one mistake a return note can never recover. */
  damageInvalid(line: DraftLine): boolean {
    return line.parentKey !== null && this.touched.missing('dm' + line.key, line.damageUnitPrice > 0 ? 'ok' : '');
  }

  /**
   * The admin's multiplier is a floor for a signed overdue price: below it the server refuses,
   * naming the figure, so the form refuses first and names the same one. (The floor is never
   * under the hire price, since the multiplier is never under 1.)
   */
  overdueInvalid(line: DraftLine): boolean {
    return line.overdueUnitPrice !== null && line.overdueUnitPrice < this.derivedOverdue(line);
  }

  /** A negative deposit is a typo, not a debt owed before anything happened. Off, the field does not exist. */
  readonly depositInvalid = computed(() => this.settings().depositEnabled && this.deposit() < 0);

  /** What is filled in and wrong in the signatory's identity; empty parts are never a problem here. */
  readonly identityProblem = computed(() => identityProblems(this.identity(), localToday()));

  private problem(): boolean {
    return (
      // Hidden while the person signs himself: what his record holds is the server's to check.
      (!this.selfSigns() && Object.keys(this.identityProblem()).length > 0) ||
      !this.customerId() ||
      !this.branchId() ||
      (this.siteRequired() && !this.siteId()) ||
      this.depositInvalid() ||
      this.lines().length === 0 ||
      this.lines().some(
        (l) =>
          !l.productId ||
          !(l.qty > 0) ||
          (l.parentKey === null && !(l.durationDays >= 1)) ||
          (l.parentKey !== null && !(l.damageUnitPrice > 0)) ||
          this.overdueInvalid(l),
      )
    );
  }

  branchName(branch: AdminBranch): string {
    return localizedName(branch, this.i18n.lang());
  }

  productName(product: AdminProduct): string {
    return localizedName(product, this.i18n.lang());
  }

  private readonly chrome = inject(PageChrome, { optional: true });
  /** Names this screen in the page header. */
  private readonly named = effect(() => {
    this.chrome?.heading.set(this.i18n.t(this.contractId() ? 'rent.editContract' : 'rent.newContract'));
    this.chrome?.description.set(null);
    this.chrome?.trail.set([]);
  });

  /** Save attempts, for the error summary to re-read the form after each; changes nothing else. */
  readonly attempts = signal(0);

  async save(): Promise<void> {
    this.attempts.update((n) => n + 1);
    // A save was attempted, so every field may now say what is wrong with it.
    this.touched.touchAll(
      'customer',
      'branch',
      'site',
      ...this.lines().flatMap((l) => ['p' + l.key, 'q' + l.key, 'd' + l.key, 'dm' + l.key]),
    );
    if (this.problem()) {
      if (this.invalidLines().length) setTimeout(() => document.getElementById('rf-line-errors')?.focus());
      return;
    }

    if (!(await this.confirm.ask(saveRequest(this.i18n)))) return;

    const order = this.lines();
    const lines: RentalContractLineRequest[] = order.map((line) => ({
      productId: line.productId!,
      qty: line.qty,
      durationDays: this.durationOf(line),
      rentUnitPrice: line.parentKey === null ? line.rentUnitPrice : 0,
      damageUnitPrice: line.damageUnitPrice,
      overdueUnitPrice: line.overdueUnitPrice,
      // The parent has no id yet, so it travels as a position in this very list.
      parentLineIndex: line.parentKey === null ? null : order.findIndex((l) => l.key === line.parentKey),
    }));

    const identity = this.identity();
    const text = (value: string) => value.trim() || null;
    const request: UpsertRentalContractRequest = {
      customerId: this.customerId()!,
      branchId: this.branchId()!,
      lines,
      // Midday rather than midnight: a date field means a day, and midnight UTC is the day
      // before in every zone west of Greenwich.
      signedAtUtc: this.signedOn() ? new Date(`${this.signedOn()}T12:00:00Z`).toISOString() : null,
      notes: this.notes().trim() || null,
      // A person's name and a representative's are the server's to take from the record; they
      // travel anyway so the server can match the representative chosen from the history.
      signatoryName: text(this.signatoryName()),
      signatoryIdNumber: text(identity.idNumber),
      signatoryIdType: text(identity.idType),
      signatoryIdIssuer: text(identity.idIssuer),
      signatoryIdIssuedOn: text(identity.idIssuedOn),
      signatoryBirthDate: text(identity.birthDate),
      siteId: this.siteId(),
      truckType: text(this.truckType()),
      truckNumber: text(this.truckNumber()),
      truckOwnerPhone: text(this.truckOwnerPhone()),
      // Always sent, so an edit says what it prints: '' is the standard wording.
      wordingKey: this.wordingKey(),
      // Zero while the switch is off: a figure typed before it was turned off must not travel.
      depositAmount: this.settings().depositEnabled ? this.deposit() : 0,
      signedByAgent: this.agentSigns(),
      // Always sent; the server refuses an untaxed one from whoever may not make it so.
      taxMode: this.taxMode(),
    };

    this.busy.set(true);
    this.error.set(null);
    try {
      const id = this.contractId();
      const contract = id ? await this.api.updateContract(id, request) : await this.api.createContract(request);
      this.saved.emit(contract.id);
    } catch (e) {
      this.error.set(adminErrorMessage(e));
      setTimeout(() => document.getElementById('rf-server-error')?.focus());
    } finally {
      this.busy.set(false);
    }
  }
}
