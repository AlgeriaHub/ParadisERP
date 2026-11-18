import { Component, OnInit, computed, inject, input, signal } from '@angular/core';
import { FormsModule } from '@angular/forms';
import {
  AdminBranch,
  AdminService,
  BranchAvailability,
  StockState,
  SupplierOption,
  adminErrorMessage,
} from '../../core/admin.service';
import { CatalogStore } from '../../core/catalog-store';
import { BranchLock } from '../../core/branch-lock';
import { I18nService } from '../../core/i18n.service';
import { ConfirmService } from '../../ui/confirm.service';
import { actionRequest } from '../../ui/confirm-requests';
import { localizedName } from './backoffice-format';
import { ProductPickerComponent } from '../../shared/product-picker.component';
import { FoldComponent } from '../../ui/fold.component';
import { OpeningImportComponent } from './opening-import.component';

type Section = 'receiving' | 'states' | 'opening' | 'availability';

/** Every state, in the order the business thinks about them, with whether it is still an asset. */
const STATE_ROWS: { state: StockState; asset: boolean }[] = [
  { state: 'Available', asset: true },
  { state: 'OnRent', asset: true },
  { state: 'Maintenance', asset: true },
  { state: 'Scrap', asset: true },
  { state: 'WrittenOff', asset: false },
];

/**
 * Where goods come from and what state they are in.
 *
 * <p>Goods from a supplier enter at the branch that receives them (owner, 2026-10-12); a branch
 * user receives into their own branch, shown as text, and the Admin chooses the branch, the
 * main warehouse by default; goods move between branches by transfer.</p>
 *
 * <p>The five states are shown as a table of words rather than a coloured chart. There are
 * five of them and the palette holds three state hues; inventing two more to fill a chart
 * would make the colour the carrier of a meaning the words already carry better.</p>
 */
@Component({
  selector: 'app-backoffice-stock',
  standalone: true,
  imports: [FormsModule, FoldComponent, ProductPickerComponent, OpeningImportComponent],
  template: `
    <div class="bo-stack">
      @if (error()) {
        <p class="notice danger" role="alert">{{ error() }}</p>
      }
      @if (done()) {
        <p class="notice ok" role="status">{{ done() }}</p>
      }

      @if (!page()) {
        <nav class="bo-chips" [attr.aria-label]="i18n.t('backoffice.tabs.stock')">
          <button
            type="button"
            [class.is-on]="section() === 'receiving'"
            [attr.aria-current]="section() === 'receiving' ? 'true' : null"
            (click)="show('receiving')"
          >
            {{ i18n.t('stock.receiving') }}
          </button>
          <button
            type="button"
            [class.is-on]="section() === 'availability'"
            [attr.aria-current]="section() === 'availability' ? 'true' : null"
            (click)="show('availability')"
          >
            {{ i18n.t('stock.availability') }}
          </button>
          <button
            type="button"
            [class.is-on]="section() === 'states'"
            [attr.aria-current]="section() === 'states' ? 'true' : null"
            (click)="show('states')"
          >
            {{ i18n.t('stock.states') }}
          </button>
          <button
            type="button"
            [class.is-on]="section() === 'opening'"
            [attr.aria-current]="section() === 'opening' ? 'true' : null"
            (click)="show('opening')"
          >
            {{ i18n.t('stock.opening') }}
          </button>
        </nav>
      }

      <!-- ---------- receiving ---------- -->
      @if (section() === 'receiving') {
        <p class="rule">{{ i18n.t('stock.warehouseRule') }}</p>

        <section class="bo-panel bo-panel--operational">
          <div class="bo-panel__head">
            <p class="bo-panel__note">{{ i18n.t('stock.receivingHint') }}</p>
          </div>
          <div class="bo-panel__body bo-form">
            <app-fold class="bo-group" [heading]="i18n.t('stock.what')" [open]="true">
              <div class="rcv-top-grid">
                <div class="bo-field rcv-field-product">
                  <label for="st-product">
                    {{ i18n.t('products.title') }}<span class="bo-req">{{ i18n.t('common.required') }}</span>
                  </label>
                  <app-product-picker
                    [inputId]="'st-product'"
                    [products]="products()"
                    [refresh]="refresh"
                    [productId]="receive.productId"
                    (picked)="receive.productId = $event.id"
                  />
                </div>
              </div>

              <div class="rcv-details-grid">
                <div class="bo-field" [class.is-invalid]="qtyInvalid('receive', receive.qty)">
                  <label for="st-qty">
                    {{ i18n.t('register.qty') }}<span class="bo-req">{{ i18n.t('common.required') }}</span>
                  </label>
                  <input
                    id="st-qty"
                    type="number"
                    min="0"
                    class="num mono"
                    [(ngModel)]="receive.qty"
                    [attr.aria-invalid]="qtyInvalid('receive', receive.qty) ? 'true' : null"
                    [attr.aria-describedby]="qtyInvalid('receive', receive.qty) ? 'st-qty-error' : null"
                    (blur)="touch('receive')"
                  />
                  @if (qtyInvalid('receive', receive.qty)) {
                    <span id="st-qty-error" class="bo-error" role="alert">{{ i18n.t('stock.qtyRequired') }}</span>
                  }
                </div>
                @if (lock.locked()) {
                  <div class="bo-field">
                    <p class="bo-fixed-label">{{ i18n.t('branchLock.branch') }}</p>
                    <p class="bo-fixed" id="str-fixed">{{ lock.ownBranchName(branches()) }}</p>
                    <span class="bo-hint">{{ i18n.t('branchLock.fixedHint') }}</span>
                  </div>
                } @else {
                  <div class="bo-field">
                    <label for="st-rbranch">{{ i18n.t('backoffice.devices.branch') }}</label>
                    <select id="st-rbranch" [(ngModel)]="receive.branchId">
                      @for (b of branches(); track b.id) {
                        <option [ngValue]="b.id">{{ name(b) }}</option>
                      }
                    </select>
                  </div>
                }
                <div class="bo-field">
                  <label for="st-supplier">
                    {{ i18n.t('stock.supplier') }}<span class="bo-req">{{ i18n.t('common.optional') }}</span>
                  </label>
                  <select id="st-supplier" [(ngModel)]="receive.supplierId">
                    <option [ngValue]="null">—</option>
                    @for (s of suppliers(); track s.id) {
                      <option [ngValue]="s.id">{{ s.name }} ({{ s.code }})</option>
                    }
                  </select>
                </div>
                <div class="bo-field">
                  <label for="st-ref">
                    {{ i18n.t('treasury.reference') }}<span class="bo-req">{{ i18n.t('common.optional') }}</span>
                  </label>
                  <input id="st-ref" [(ngModel)]="receive.reference" />
                </div>
              </div>
            </app-fold>

            <!-- Folded at first: left blank, the stored values stay (the note says so, folded or not). -->
            <app-fold class="bo-group" [heading]="i18n.t('stock.prices')">
              <p class="bo-group__note" foldLead>{{ i18n.t('stock.assetValueHint') }}</p>
              <div class="rcv-prices-grid">
                <div class="bo-field">
                  <label for="st-cost">{{ i18n.t('stock.unitCost') }}</label>
                  <input id="st-cost" type="number" min="0" class="num mono" [(ngModel)]="receive.unitCost" />
                </div>
                <div class="bo-field">
                  <label for="st-asset">{{ i18n.t('stock.assetValue') }}</label>
                  <input id="st-asset" type="number" min="0" class="num mono" [(ngModel)]="receive.assetUnitValue" />
                </div>
              </div>
            </app-fold>
          </div>
          <div class="bo-panel__foot">
            <button type="button" class="btn-primary" [disabled]="busy()" (click)="receiveStock()">
              {{ i18n.t('stock.receive') }}
            </button>
          </div>
        </section>
      }

      <!-- ---------- availability ---------- -->
      @if (section() === 'availability') {
        @if (ownRows().length) {
          <div class="bo-metrics">
            <div class="bo-metric bo-metric--lead">
              <span class="bo-metric__label">{{ i18n.t('stock.state.available') }}</span>
              <strong class="bo-metric__value mono">{{ totals().available }}</strong>
              <span class="bo-metric__note"><span>{{ lock.locked() ? lock.ownBranchName(branches()) : i18n.t('stock.acrossBranches') }}</span></span>
            </div>
            <div class="bo-metric">
              <span class="bo-metric__label">{{ i18n.t('stock.state.onrent') }}</span>
              <strong class="bo-metric__value mono">{{ totals().onRent }}</strong>
            </div>
            <div class="bo-metric">
              <span class="bo-metric__label">{{ i18n.t('stock.state.maintenance') }}</span>
              <strong class="bo-metric__value mono">{{ totals().maintenance }}</strong>
            </div>
            <div class="bo-metric">
              <span class="bo-metric__label">{{ i18n.t('stock.totalOwned') }}</span>
              <strong class="bo-metric__value mono">{{ totals().owned }}</strong>
            </div>
          </div>
        }

        <section class="bo-panel">
          <div class="bo-panel__head">
            <p class="bo-panel__note">{{ i18n.t('stock.availabilityHint') }}</p>
          </div>
          <div class="bo-panel__body">
            <div class="bo-field">
              <label for="st-lookup">{{ i18n.t('products.title') }}</label>
              <app-product-picker
                inputId="st-lookup"
                [products]="products()"
                [refresh]="refresh"
                [productId]="lookupProductId"
                catchScans
                (picked)="lookupProductId = $event.id; lookup()"
              />
            </div>
          </div>

          @if (ownRows().length) {
            <div class="bo-scroll">
              <table class="bo-data">
                <thead>
                  <tr>
                    <th>{{ i18n.t('backoffice.devices.branch') }}</th>
                    <th class="num">{{ i18n.t('stock.state.available') }}</th>
                    <th class="num">{{ i18n.t('stock.state.onrent') }}</th>
                    <th class="num">{{ i18n.t('stock.state.maintenance') }}</th>
                    <th class="num">{{ i18n.t('stock.state.scrap') }}</th>
                    <th class="num">{{ i18n.t('stock.owned') }}</th>
                  </tr>
                </thead>
                <tbody>
                  @for (row of ownRows(); track row.branchId) {
                    <tr>
                      <td [attr.data-label]="i18n.t('backoffice.devices.branch')">
                        {{ name(row) }}
                        @if (row.isMain && row.isWarehouse) {
                          <span class="bo-state bo-state--lead">{{ i18n.t('stock.mainWarehouse') }}</span>
                        }
                      </td>
                      <td class="num mono bo-cell-strong" [attr.data-label]="i18n.t('stock.state.available')">{{ row.available }}</td>
                      <td class="num mono" [attr.data-label]="i18n.t('stock.state.onrent')">{{ row.onRent }}</td>
                      <td class="num mono" [attr.data-label]="i18n.t('stock.state.maintenance')">{{ row.maintenance }}</td>
                      <td class="num mono" [attr.data-label]="i18n.t('stock.state.scrap')">{{ row.scrap }}</td>
                      <td class="num mono" [attr.data-label]="i18n.t('stock.owned')">{{ row.owned }}</td>
                    </tr>
                  }
                </tbody>
                <tfoot>
                  <tr>
                    <td [attr.data-label]="i18n.t('backoffice.devices.branch')">{{ i18n.t('common.total') }}</td>
                    <td class="num mono" [attr.data-label]="i18n.t('stock.state.available')">{{ totals().available }}</td>
                    <td class="num mono" [attr.data-label]="i18n.t('stock.state.onrent')">{{ totals().onRent }}</td>
                    <td class="num mono" [attr.data-label]="i18n.t('stock.state.maintenance')">{{ totals().maintenance }}</td>
                    <td class="num mono" [attr.data-label]="i18n.t('stock.state.scrap')">{{ totals().scrap }}</td>
                    <td class="num mono" [attr.data-label]="i18n.t('stock.totalOwned')">{{ totals().owned }}</td>
                  </tr>
                </tfoot>
              </table>
            </div>
          } @else if (busy()) {
            <p class="bo-loading" role="status">
              <span class="bo-loading__word">{{ i18n.t('common.loading') }}</span>
              <span class="bo-loading__rows" aria-hidden="true"></span>
            </p>
          } @else {
            <p class="bo-empty">
              <strong>{{ i18n.t('common.nothingHere') }}</strong>
              {{ i18n.t('stock.chooseProduct') }}
            </p>
          }
          @if (lock.locked() && lock.mayLookUpOtherBranches()) {
            <div class="bo-panel__body" id="st-other-availability">
              <div class="bo-group">
                <h3 class="bo-group__title">{{ i18n.t('branchLock.otherAvailability') }}</h3>
                <p class="bo-group__note">{{ i18n.t('branchLock.otherAvailabilityHint') }}</p>
                @if (otherRows().length) {
                  <div class="bo-scroll">
                    <table class="bo-data">
                      <thead>
                        <tr>
                          <th>{{ i18n.t('branchLock.branch') }}</th>
                          <th class="num">{{ i18n.t('branchLock.available') }}</th>
                        </tr>
                      </thead>
                      <tbody>
                        @for (row of otherRows(); track row.branchId) {
                          <tr>
                            <td [attr.data-label]="i18n.t('branchLock.branch')">{{ name(row) }}</td>
                            <td class="num mono bo-cell-strong" [attr.data-label]="i18n.t('branchLock.available')">{{ row.available }}</td>
                          </tr>
                        }
                      </tbody>
                    </table>
                  </div>
                } @else {
                  <p class="bo-hint">{{ i18n.t('branchLock.otherAvailabilityNone') }}</p>
                }
              </div>
            </div>
          }
        </section>
      }

      <!-- ---------- states ---------- -->
      @if (section() === 'states') {
        <section class="bo-panel">
          <div class="bo-panel__head">
            <h2 class="bo-panel__title">{{ i18n.t('stock.fiveStates') }}</h2>
            <p class="bo-panel__note">{{ i18n.t('stock.statesHint') }}</p>
          </div>
          <div class="bo-scroll">
            <table class="bo-data">
              <thead>
                <tr>
                  <th>{{ i18n.t('common.state') }}</th>
                  <th>{{ i18n.t('common.meaning') }}</th>
                  <th>{{ i18n.t('stock.assetValue') }}</th>
                </tr>
              </thead>
              <tbody>
                @for (row of stateRows; track row.state) {
                  <tr>
                    <td [attr.data-label]="i18n.t('common.state')">
                      <span class="bo-state" [class.bo-state--ok]="row.state === 'Available'" [class.bo-state--danger]="!row.asset">
                        {{ i18n.t('stock.state.' + row.state.toLowerCase()) }}
                      </span>
                    </td>
                    <td [attr.data-label]="i18n.t('common.meaning')">{{ i18n.t('stock.note.' + row.state.toLowerCase()) }}</td>
                    <td [attr.data-label]="i18n.t('stock.assetValue')">{{ i18n.t(row.asset ? 'stock.inAssets' : 'stock.outOfAssets') }}</td>
                  </tr>
                }
              </tbody>
            </table>
          </div>
        </section>

        <section class="bo-panel">
          <div class="bo-panel__head">
            <h2 class="bo-panel__title">{{ i18n.t('stock.moveState') }}</h2>
          </div>
          <div class="bo-panel__body bo-form">
            <app-fold class="bo-group" [heading]="i18n.t('stock.where')" [open]="true">
              <div class="bo-fields bo-fields--roomy">
                <div class="bo-field bo-field--wide">
                  <label for="st-mproduct">{{ i18n.t('products.title') }}</label>
                  <app-product-picker
                    [inputId]="'st-mproduct'"
                    [products]="products()"
                    [refresh]="refresh"
                    [productId]="move.productId"
                    (picked)="move.productId = $event.id"
                  />
                </div>
                @if (lock.locked()) {
                  <div class="bo-field">
                    <p class="bo-fixed-label">{{ i18n.t('branchLock.branch') }}</p>
                    <p class="bo-fixed" id="stm-fixed">{{ lock.ownBranchName(branches()) }}</p>
                    <span class="bo-hint">{{ i18n.t('branchLock.fixedHint') }}</span>
                  </div>
                } @else {
                  <div class="bo-field">
                    <label for="st-mbranch">{{ i18n.t('backoffice.devices.branch') }}</label>
                    <select id="st-mbranch" [(ngModel)]="move.branchId">
                      @for (b of branches(); track b.id) {
                        <option [ngValue]="b.id">{{ name(b) }}</option>
                      }
                    </select>
                  </div>
                }
              </div>
            </app-fold>

            <app-fold class="bo-group" [heading]="i18n.t('stock.movement')" [open]="true">
              <div class="bo-fields">
                <div class="bo-field" [class.is-invalid]="qtyInvalid('move', move.qty)">
                  <label for="st-mqty">
                    {{ i18n.t('register.qty') }}<span class="bo-req">{{ i18n.t('common.required') }}</span>
                  </label>
                  <input
                    id="st-mqty"
                    type="number"
                    min="0"
                    class="num mono"
                    [(ngModel)]="move.qty"
                    [attr.aria-invalid]="qtyInvalid('move', move.qty) ? 'true' : null"
                    [attr.aria-describedby]="qtyInvalid('move', move.qty) ? 'st-mqty-error' : null"
                    (blur)="touch('move')"
                  />
                  @if (qtyInvalid('move', move.qty)) {
                    <span id="st-mqty-error" class="bo-error" role="alert">{{ i18n.t('stock.qtyRequired') }}</span>
                  }
                </div>
                <div class="bo-field">
                  <label for="st-mfrom">{{ i18n.t('stock.from') }}</label>
                  <select id="st-mfrom" [(ngModel)]="move.from">
                    @for (s of sourceStates; track s) {
                      <option [ngValue]="s">{{ i18n.t('stock.state.' + s.toLowerCase()) }}</option>
                    }
                  </select>
                </div>
                <div class="bo-field" [class.is-invalid]="move.from === move.to">
                  <label for="st-mto">{{ i18n.t('stock.to') }}</label>
                  <select
                    id="st-mto"
                    [(ngModel)]="move.to"
                    [attr.aria-invalid]="move.from === move.to ? 'true' : null"
                    [attr.aria-describedby]="move.from === move.to ? 'st-mto-error' : null"
                  >
                    @for (s of allStates; track s) {
                      <option [ngValue]="s">{{ i18n.t('stock.state.' + s.toLowerCase()) }}</option>
                    }
                  </select>
                  @if (move.from === move.to) {
                    <span id="st-mto-error" class="bo-error" role="alert">{{ i18n.t('stock.sameState') }}</span>
                  }
                </div>
                <div class="bo-field bo-field--wide">
                  <label for="st-mreason">{{ i18n.t('stock.reason') }}</label>
                  <input id="st-mreason" [(ngModel)]="move.reason" />
                </div>
              </div>
              @if (move.to === 'WrittenOff') {
                <p class="notice warn">{{ i18n.t('stock.writeOffWarning') }}</p>
              }
              <p class="bo-panel__note">{{ i18n.t('stock.onRentByContract') }}</p>
            </app-fold>

            @if (balances(); as b) {
              <div class="bo-group">
                <h3 class="bo-group__title">{{ i18n.t('stock.newBalance') }}</h3>
                <div class="bo-metrics">
                  <div class="bo-metric">
                    <span class="bo-metric__label">{{ i18n.t('stock.state.available') }}</span>
                    <strong class="bo-metric__value mono">{{ b.available }}</strong>
                  </div>
                  <div class="bo-metric">
                    <span class="bo-metric__label">{{ i18n.t('stock.state.onrent') }}</span>
                    <strong class="bo-metric__value mono">{{ b.onRent }}</strong>
                  </div>
                  <div class="bo-metric">
                    <span class="bo-metric__label">{{ i18n.t('stock.state.maintenance') }}</span>
                    <strong class="bo-metric__value mono">{{ b.maintenance }}</strong>
                  </div>
                  <div class="bo-metric">
                    <span class="bo-metric__label">{{ i18n.t('stock.state.scrap') }}</span>
                    <strong class="bo-metric__value mono">{{ b.scrap }}</strong>
                  </div>
                </div>
              </div>
            }
          </div>
          <div class="bo-panel__foot">
            <button type="button" class="btn-primary" [disabled]="busy() || move.from === move.to" (click)="changeState()">
              {{ i18n.t('stock.moveState') }}
            </button>
          </div>
        </section>
      }

      <!-- ---------- opening balance ---------- -->
      @if (section() === 'opening') {
        <p class="rule">{{ i18n.t('stock.warehouseRule') }}</p>

        <section class="bo-panel">
          <div class="bo-panel__head">
            <p class="bo-panel__note">{{ i18n.t('stock.openingHint') }}</p>
          </div>
          <div class="bo-panel__body">
            @if (setupBranches().length) {
              <div class="bo-fields bo-fields--roomy">
                @if (lock.locked()) {
                  <div class="bo-field">
                    <p class="bo-fixed-label">{{ i18n.t('branchLock.branch') }}</p>
                    <p class="bo-fixed" id="sto-fixed">{{ lock.ownBranchName(branches()) }}</p>
                    <span class="bo-hint">{{ i18n.t('stock.inSetup') }}</span>
                  </div>
                } @else {
                  <div class="bo-field">
                    <label for="st-obranch">{{ i18n.t('backoffice.devices.branch') }}</label>
                    <select id="st-obranch" [(ngModel)]="opening.branchId">
                      @for (b of setupBranches(); track b.id) {
                        <option [ngValue]="b.id">{{ name(b) }}</option>
                      }
                    </select>
                    <p class="bo-hint">{{ i18n.t('stock.branchState') }} — {{ i18n.t('stock.inSetup') }}</p>
                  </div>
                }
                <div class="bo-field bo-field--wide">
                  <label for="st-oproduct">{{ i18n.t('products.title') }}</label>
                  <app-product-picker
                    [inputId]="'st-oproduct'"
                    [products]="products()"
                    [refresh]="refresh"
                    [productId]="opening.productId"
                    (picked)="opening.productId = $event.id"
                  />
                </div>
                <div class="bo-field" [class.is-invalid]="qtyInvalid('opening', opening.qty)">
                  <label for="st-oqty">
                    {{ i18n.t('register.qty') }}<span class="bo-req">{{ i18n.t('common.required') }}</span>
                  </label>
                  <input
                    id="st-oqty"
                    type="number"
                    min="0"
                    class="num mono"
                    [(ngModel)]="opening.qty"
                    [attr.aria-invalid]="qtyInvalid('opening', opening.qty) ? 'true' : null"
                    [attr.aria-describedby]="qtyInvalid('opening', opening.qty) ? 'st-oqty-error' : null"
                    (blur)="touch('opening')"
                  />
                  @if (qtyInvalid('opening', opening.qty)) {
                    <span id="st-oqty-error" class="bo-error" role="alert">{{ i18n.t('stock.qtyRequired') }}</span>
                  }
                </div>
              </div>
              <p class="bo-hint">{{ i18n.t('stock.declareOpenHint') }}</p>
            } @else {
              <p class="notice warn">{{ i18n.t('stock.allBranchesOpen') }}</p>
            }
          </div>
          <div class="bo-panel__foot">
            <button type="button" class="btn-ghost" [disabled]="busy() || !opening.branchId" (click)="openBranch()">
              {{ i18n.t('stock.declareOpen') }}
            </button>
            <button
              type="button"
              class="btn-primary"
              [disabled]="busy() || !setupBranches().length"
              (click)="setOpening()"
            >
              {{ i18n.t('common.save') }}
            </button>
          </div>
        </section>

        @if (setupBranchList().length) {
          <app-opening-import [branches]="setupBranchList()" />
        }
      }
    </div>
  `,
  styleUrls: ['./backoffice-ui.scss'],
  styles: [
    `
      /* The warehouse rule is the point of the screen, so it is stated, not implied. */
      .rule {
        margin: 0;
        padding: 0.65rem 0.85rem;
        border-radius: var(--radius-card);
        border: 1px solid color-mix(in srgb, var(--accent) 25%, transparent);
        background: var(--surface-tint);
        color: var(--accent);
        font-size: 0.88rem;
        font-weight: 600;
      }
      .bo-state + .bo-state,
      td > .bo-state:not(:first-child) {
        margin-inline-start: 0.35rem;
      }
      .bo-panel.bo-panel--operational {
        max-inline-size: min(64rem, 100%);
        margin-inline: 0;

        .bo-form {
          max-inline-size: 100%;
        }
      }
      .rcv-top-grid {
        display: grid;
        grid-template-columns: 1fr;
        gap: var(--sp-3);
        inline-size: 100%;
      }
      .rcv-details-grid {
        display: grid;
        grid-template-columns: 1fr;
        gap: var(--sp-3) var(--sp-4);
        inline-size: 100%;

        @media (min-width: 640px) {
          grid-template-columns: repeat(2, 1fr);
        }

        @media (min-width: 1024px) {
          grid-template-columns: 0.9fr 1.3fr 1.3fr 1.1fr;
        }
      }
      .rcv-prices-grid {
        display: grid;
        grid-template-columns: 1fr;
        gap: var(--sp-3) var(--sp-4);
        max-inline-size: 32rem;

        @media (min-width: 640px) {
          grid-template-columns: repeat(2, 1fr);
        }
      }
    `,
  ],
})
export class BackofficeStockComponent implements OnInit {
  readonly i18n = inject(I18nService);
  readonly lock = inject(BranchLock);
  private readonly admin = inject(AdminService);
  private readonly confirm = inject(ConfirmService);

  /**
   * The chip the route opened this section on (core/nav.ts). Set, the sidebar is the navigation
   * and this component's own chip strip is not drawn; unset, it behaves as it always did.
   */
  readonly page = input<Section | null>(null);
  readonly section = signal<Section>('receiving');
  readonly error = signal<string | null>(null);
  readonly done = signal<string | null>(null);
  readonly busy = signal(false);

  /** The session's one copy of the catalogue; see CatalogStore for when it is refreshed. */
  private readonly catalog = inject(CatalogStore);
  readonly products = this.catalog.products;
  readonly branches = signal<AdminBranch[]>([]);
  readonly suppliers = signal<SupplierOption[]>([]);
  /** What a picker calls when a scan matches nothing here. */
  readonly refresh = () => this.catalog.refresh();
  readonly availability = signal<BranchAvailability[]>([]);
  /** Balances the server reported after the last state change, shown rather than described. */
  readonly balances = signal<{ available: number; onRent: number; maintenance: number; scrap: number } | null>(null);

  readonly stateRows = STATE_ROWS;

  /**
   * Which forms have been left once. A required field that turns red the moment the screen
   * opens is nagging; one that stays silent after a refused save is hiding.
   */
  private readonly touched = new Set<string>();

  touch(form: string): void {
    this.touched.add(form);
  }

  qtyInvalid(form: string, qty: number | null): boolean {
    return this.touched.has(form) && !(Number(qty) > 0);
  }

  /** A catalogue or branch record named in the reader's language, not always in French. */
  name(record: { nameAr: string; nameFr: string; nameEn: string }): string {
    return localizedName(record, this.i18n.lang());
  }

  /** WrittenOff is missing on purpose: stock that has left cannot be moved back out of it. */
  /** On rent is not offered: goods go out and come back only through a contract's notes (owner, 2026-10-12). */
  readonly sourceStates: StockState[] = ['Available', 'Maintenance', 'Scrap'];
  readonly allStates: StockState[] = ['Available', 'Maintenance', 'Scrap', 'WrittenOff'];

  /**
   * The rows read in full: every branch for the Admin, their own for anyone else. Another branch
   * comes to a non-admin only with the permission, and only as an available quantity (otherRows).
   */
  readonly ownRows = computed(() => {
    const own = this.lock.lockedBranchId();
    return own === null ? this.availability() : this.availability().filter((r) => r.branchId === own);
  });
  readonly otherRows = computed(() => {
    const own = this.lock.lockedBranchId();
    return own === null ? [] : this.availability().filter((r) => r.branchId !== own);
  });

  readonly totals = computed(() =>
    this.ownRows().reduce(
      (sum, row) => ({
        available: sum.available + row.available,
        onRent: sum.onRent + (row.onRent ?? 0),
        maintenance: sum.maintenance + (row.maintenance ?? 0),
        scrap: sum.scrap + (row.scrap ?? 0),
        owned: sum.owned + (row.owned ?? 0),
      }),
      { available: 0, onRent: 0, maintenance: 0, scrap: 0, owned: 0 },
    ),
  );

  receive = {
    productId: '',
    branchId: '',
    qty: 0,
    unitCost: null as number | null,
    assetUnitValue: null as number | null,
    reference: '',
    supplierId: null as string | null,
  };
  move = { productId: '', branchId: '', qty: 0, from: 'Available' as StockState, to: 'Maintenance' as StockState, reason: '' };
  opening = { branchId: '', productId: '', qty: 0 };
  lookupProductId = '';

  async ngOnInit(): Promise<void> {
    await this.run(async () => {
      const [products, branches] = await Promise.all([this.catalog.ensure(), this.admin.listBranches()]);
      this.branches.set(branches);
      this.receive.branchId ||= branches.find((b) => b.isMain && b.isWarehouse)?.id ?? this.lock.defaultBranch(branches) ?? '';
      this.receive.productId ||= products[0]?.id ?? '';
      this.move.productId ||= products[0]?.id ?? '';
      this.move.branchId ||= this.lock.defaultBranch(branches) ?? '';
      this.opening.productId ||= products[0]?.id ?? '';
      this.opening.branchId ||= this.setupBranches()[0]?.id ?? '';
      this.lookupProductId ||= products[0]?.id ?? '';
    });
    const page = this.page();
    if (page) {
      this.show(page);
    } else {
      void this.loadSuppliers();
    }
  }

  /** Only a branch still in setup may be given an opening balance — and only their own, for a non-admin. */
  setupBranches(): AdminBranch[] {
    return this.lock.scope(this.branches()).filter((b) => !b.isOpenForOperation);
  }

  /** The same list held still between change detections, for the sheet import's input. */
  readonly setupBranchList = computed(() => this.setupBranches());

  show(section: Section): void {
    this.section.set(section);
    this.error.set(null);
    this.done.set(null);
    if (section === 'receiving') void this.loadSuppliers();
    if (section === 'availability' && !this.availability().length) void this.lookup();
  }

  async loadSuppliers(): Promise<void> {
    try {
      this.suppliers.set(await this.admin.listSuppliers());
    } catch {
      // a load failure leaves just the empty option, never blocks receiving
      this.suppliers.set([]);
    }
  }

  async receiveStock(): Promise<void> {
    this.touch('receive');
    if (!(Number(this.receive.qty) > 0)) return;
    if (!(await this.confirm.ask(actionRequest(this.i18n, this.i18n.t('stock.receive'))))) return;
    await this.run(async () => {
      await this.admin.receiveStock(
        [
          {
            productId: this.receive.productId,
            qty: Number(this.receive.qty) || 0,
            unitCost: numberOrNull(this.receive.unitCost),
            assetUnitValue: numberOrNull(this.receive.assetUnitValue),
          },
        ],
        this.receive.reference || null,
        this.receive.supplierId || null,
        this.lock.locked() ? null : this.receive.branchId || null,
      );
      this.done.set(this.i18n.t('stock.received'));
      this.receive.qty = 0;
      this.receive.supplierId = null;
    });
  }

  async changeState(): Promise<void> {
    this.touch('move');
    if (!(Number(this.move.qty) > 0)) return;
    if (!(await this.confirm.ask(actionRequest(this.i18n, this.i18n.t('stock.moveState'))))) return;
    await this.run(async () => {
      const result = await this.admin.changeStockState({
        productId: this.move.productId,
        branchId: this.move.branchId,
        qty: Number(this.move.qty) || 0,
        from: this.move.from,
        to: this.move.to,
        reason: this.move.reason || null,
      });
      this.balances.set({
        available: result.available,
        onRent: result.onRent,
        maintenance: result.maintenance,
        scrap: result.scrap,
      });
      this.done.set(this.i18n.t('common.saved'));
      this.move.qty = 0;
    });
  }

  async setOpening(): Promise<void> {
    this.touch('opening');
    if (!(Number(this.opening.qty) > 0)) return;
    if (!(await this.confirm.ask(actionRequest(this.i18n, this.i18n.t('common.save'))))) return;
    await this.run(async () => {
      await this.admin.openingBalance(this.opening.branchId, [
        { productId: this.opening.productId, qty: Number(this.opening.qty) || 0 },
      ]);
      this.done.set(this.i18n.t('common.saved'));
    });
  }

  async openBranch(): Promise<void> {
    const branch = this.branches().find((b) => b.id === this.opening.branchId);
    const open = await this.confirm.ask({
      title: this.i18n.t('stock.declareOpenTitle', { name: branch ? this.name(branch) : '' }),
      message: this.i18n.t('stock.declareOpenConfirm'),
      confirmLabel: this.i18n.t('stock.declareOpen'),
    });
    if (!open) return;
    await this.run(async () => {
      await this.admin.openBranchForOperation(this.opening.branchId);
      this.branches.set(await this.admin.listBranches());
      this.opening.branchId = this.setupBranches()[0]?.id ?? '';
      this.done.set(this.i18n.t('common.saved'));
    });
  }

  async lookup(): Promise<void> {
    if (!this.lookupProductId) return;
    await this.run(async () => this.availability.set(await this.admin.availability(this.lookupProductId)));
  }

  private async run(action: () => Promise<unknown>): Promise<void> {
    this.busy.set(true);
    try {
      await action();
      this.error.set(null);
    } catch (e) {
      this.error.set(adminErrorMessage(e));
      this.done.set(null);
    } finally {
      this.busy.set(false);
    }
  }
}

/** Blank means "leave the stored figure alone", which is not the same as zero. */
function numberOrNull(value: number | null): number | null {
  return value === null || value === undefined || (value as unknown as string) === '' ? null : Number(value);
}
