import { NgTemplateOutlet } from '@angular/common';
import { Component, OnInit, computed, effect, inject, signal } from '@angular/core';
import { FormsModule } from '@angular/forms';
import { AdminService, UpsertStoreRequest, adminErrorMessage } from '../../core/admin.service';
import { AuthService } from '../../core/auth.service';
import { mayDo } from '../../core/landing';
import { I18nService } from '../../core/i18n.service';
import { Lang } from '../../core/models';
import { PosService } from '../../core/pos.service';
import { RegionalService } from '../../core/regional.service';
import { SecureImageService, readAsDataUrl } from '../../core/secure-image.service';
import { SYSTEM_FONT, UI_FONTS, uiFontStack } from '../../core/ui-fonts';
import { ConfirmService } from '../../ui/confirm.service';
import { saveRequest } from '../../ui/confirm-requests';
import { DialogComponent } from '../../ui/dialog.component';
import { FoldComponent } from '../../ui/fold.component';
import { CONTRACT_FIELDS, companyIdField, contractFieldsFor, fieldToken, insertToken } from '../print/contract-fields';
import { ContractPreviewComponent } from '../print/contract-preview.component';
import {
  CONTRACT_WORDING_NAME_MAX,
  ContractWording,
  DOCUMENT_TEXT_KINDS,
  DOCUMENT_TEXT_MAX,
  DocumentTextAllowance,
  DocumentTextChange,
  DocumentTextKey,
  LOGO_MAX_BYTES,
  LOGO_TYPES,
  MAX_CONTRACT_WORDINGS,
  StoreDocumentsApi,
  StoreDocumentsDto,
  newWordingKey,
} from '../print/store-documents-api.service';

/**
 * The printed-documents form: one text per kind per enabled language, the three contact lines, the
 * one number that says how often a kind's wording may be rewritten in a year, and the logo.
 */
interface DocumentsForm {
  /** Per kind, and per named contract wording under its key (PinSay #302). */
  texts: Record<string, Record<string, string>>;
  /** The named contract wordings beside the standard one, in the order the contract form lists them. */
  wordings: ContractWording[];
  email: string;
  website: string;
  bankDetails: string;
  changesPerYear: number;
  /**
   * The saved logo's link (kept as it is), a picked file as a `data:` URL (replaces it), or null
   * (removes it). Sent whole on every save: leaving it out would clear it.
   */
  logoUrl: string | null;
  /** The languages every printed title is set in, in the order the interface lists them. */
  titleLanguages: string[];
  /** The language an item's name prints in on the rental papers; '' keeps French with the Arabic beneath. */
  itemName: string;
  /** A second language printed beneath it; '' for none. */
  itemNameBeneath: string;
}

@Component({
  selector: 'app-backoffice-store',
  standalone: true,
  imports: [FormsModule, DialogComponent, NgTemplateOutlet, FoldComponent, ContractPreviewComponent],
  template: `
    <div class="bo-stack store-page">
      @if (error()) {
        <p class="notice danger" role="alert">{{ error() }}</p>
      }
      @if (saved()) {
        <p class="notice ok" role="status">{{ i18n.t('common.saved') }}</p>
      }

      @if (form(); as f) {
        <section class="bo-panel">
          <div class="bo-panel__head">
            <h2 class="bo-panel__title">{{ i18n.t('store.profile.title') }}</h2>
          </div>
          <div class="bo-panel__body bo-form bo-form--full">
            <!-- One row per subject: what it is on one side, its fields on the other. Each row folds;
                 the first and the one that can hold a required field start open, the rest folded. -->
            <app-fold class="bo-group bo-group--row" [heading]="i18n.t('common.group.names')" [open]="true">
              <div class="bo-fields">
                <div class="bo-field">
                  <label for="so-fr">{{ i18n.t('field.nameFr') }}</label>
                  <input id="so-fr" [(ngModel)]="f.nameFr" />
                </div>
                <div class="bo-field">
                  <label for="so-ar">{{ i18n.t('field.nameAr') }}</label>
                  <input id="so-ar" [(ngModel)]="f.nameAr" dir="rtl" />
                </div>
                <div class="bo-field">
                  <label for="so-en">{{ i18n.t('field.nameEn') }}</label>
                  <input id="so-en" [(ngModel)]="f.nameEn" />
                </div>
              </div>
            </app-fold>

            <app-fold class="bo-group bo-group--row" [heading]="i18n.t('common.group.contact')">
              <div class="bo-fields bo-fields--roomy">
                <div class="bo-field">
                  <label for="so-address-fr">{{ i18n.t('backoffice.store.addressFr') }}</label>
                  <input id="so-address-fr" [(ngModel)]="f.address" />
                </div>
                <div class="bo-field">
                  <label for="so-address-ar">{{ i18n.t('backoffice.store.addressAr') }}</label>
                  <input id="so-address-ar" [(ngModel)]="f.addressAr" dir="rtl" />
                </div>
                <div class="bo-field">
                  <label for="so-address-en">{{ i18n.t('backoffice.store.addressEn') }}</label>
                  <input id="so-address-en" [(ngModel)]="f.addressEn" />
                </div>
                <div class="bo-field">
                  <label for="so-phone">{{ i18n.t('backoffice.store.phone') }}</label>
                  <input id="so-phone" class="mono" [(ngModel)]="f.phone" />
                </div>
                <div class="bo-field">
                  <label for="so-footer-fr">{{ i18n.t('backoffice.store.footerFr') }}</label>
                  <input id="so-footer-fr" [(ngModel)]="f.receiptFooter" />
                </div>
                <div class="bo-field">
                  <label for="so-footer-ar">{{ i18n.t('backoffice.store.footerAr') }}</label>
                  <input id="so-footer-ar" [(ngModel)]="f.receiptFooterAr" dir="rtl" />
                </div>
                <div class="bo-field">
                  <label for="so-footer-en">{{ i18n.t('backoffice.store.footerEn') }}</label>
                  <input id="so-footer-en" [(ngModel)]="f.receiptFooterEn" />
                </div>
              </div>
            </app-fold>

            <!-- Open: the country profile can make an identifier required, and Save stays disabled
                 while one is empty — so the empty one is marked, never folded out of sight. -->
            @if (profile().fiscalIds.length) {
              <app-fold class="bo-group bo-group--row" [heading]="i18n.t('backoffice.store.fiscalIds')" [open]="true">
                <div class="bo-fields">
                  @for (id of profile().fiscalIds; track id.key) {
                    <div class="bo-field" [class.is-invalid]="id.required && !(f.fiscalIds[id.key] || '').trim()">
                      <label [for]="'fiscal-' + id.key">
                        {{ i18n.text(id.labels) || id.code }}
                        @if (id.required) {
                          <span class="bo-req">{{ i18n.t('common.required') }}</span>
                        }
                      </label>
                      <input
                        [id]="'fiscal-' + id.key"
                        class="mono"
                        dir="ltr"
                        [(ngModel)]="f.fiscalIds[id.key]"
                        maxlength="40"
                        [required]="id.required"
                      />
                    </div>
                  }
                </div>
              </app-fold>
            }

            <app-fold class="bo-group bo-group--row" [heading]="i18n.t('backoffice.store.customerFields')">
              <p class="bo-group__note" foldLead>{{ i18n.t('backoffice.store.customerFieldsHint') }}</p>
              <div class="bo-fields">
                <div class="bo-check">
                  <input id="reqEmail" type="checkbox" [(ngModel)]="f.requireCustomerEmail" />
                  <label for="reqEmail">{{ i18n.t('backoffice.customers.email') }}</label>
                </div>
                <div class="bo-check">
                  <input id="reqBirthDate" type="checkbox" [(ngModel)]="f.requireCustomerBirthDate" />
                  <label for="reqBirthDate">{{ i18n.t('backoffice.customers.birthDate') }}</label>
                </div>
                <div class="bo-check">
                  <input id="reqPhone" type="checkbox" [(ngModel)]="f.requireCustomerPhone" />
                  <label for="reqPhone">{{ i18n.t('backoffice.customers.phone') }}</label>
                </div>
                <div class="bo-check">
                  <input id="reqAddress" type="checkbox" [(ngModel)]="f.requireCustomerAddress" />
                  <label for="reqAddress">{{ i18n.t('backoffice.customers.address') }}</label>
                </div>
                <!-- Asked of a company only (owner, 2026-10-05): a natural person has no tax number. -->
                <div class="bo-check">
                  <input id="reqTaxId" type="checkbox" [(ngModel)]="f.requireCustomerTaxId" />
                  <label for="reqTaxId">{{ i18n.t('backoffice.store.requireTaxId') }}</label>
                </div>
              </div>
            </app-fold>

            <app-fold class="bo-group bo-group--row" [heading]="i18n.t('backoffice.store.loyalty')">
              <p class="bo-group__note" foldLead>{{ i18n.t('backoffice.store.loyaltyHint') }}</p>
              <div class="bo-check">
                <input id="loyaltyEnabled" type="checkbox" [(ngModel)]="f.loyaltyEnabled" />
                <label for="loyaltyEnabled">{{ i18n.t('backoffice.store.loyaltyEnabled') }}</label>
              </div>
              <div class="bo-fields">
                <div class="bo-field">
                  <label for="loyaltyPerUnit">{{ i18n.t('backoffice.store.loyaltyPointsPerUnit') }}</label>
                  <input
                    id="loyaltyPerUnit"
                    type="number"
                    min="0"
                    step="0.01"
                    class="num mono"
                    [(ngModel)]="f.loyaltyPointsPerUnit"
                    [disabled]="!f.loyaltyEnabled"
                  />
                </div>
                <div class="bo-field">
                  <label for="loyaltyValue">{{ i18n.t('backoffice.store.loyaltyPointValue') }}</label>
                  <input
                    id="loyaltyValue"
                    type="number"
                    min="0"
                    step="0.01"
                    class="num mono"
                    [(ngModel)]="f.loyaltyPointValue"
                    [disabled]="!f.loyaltyEnabled"
                  />
                </div>
                <div class="bo-field">
                  <label for="loyaltyMin">{{ i18n.t('backoffice.store.loyaltyMinRedeem') }}</label>
                  <input
                    id="loyaltyMin"
                    type="number"
                    min="0"
                    step="1"
                    class="num mono"
                    [(ngModel)]="f.loyaltyMinRedeemPoints"
                    [disabled]="!f.loyaltyEnabled"
                  />
                </div>
              </div>
            </app-fold>

            <!-- The company's face on screen. One select, previewing itself: a font is the one
                 setting nobody can judge from its name, so the control is drawn in the face it
                 would apply. Receipts are printed by the terminal and never follow it. -->
            <app-fold class="bo-group bo-group--row" [heading]="i18n.t('backoffice.store.face')">
              <p class="bo-group__note" foldLead>{{ i18n.t('backoffice.store.uiFontHint') }}</p>
              <div class="bo-fields">
                <div class="bo-field">
                  <label for="so-uiFont">{{ i18n.t('backoffice.store.uiFont') }}</label>
                  <select id="so-uiFont" [style.fontFamily]="facePreview()" [ngModel]="uiFont()" (ngModelChange)="uiFont.set($event)">
                    <!-- The profile's face is named inside the sentence, not after a dash: a written
                         separator between an Arabic phrase and a Latin typeface name lands on the
                         wrong side of the run. -->
                    <option value="">{{ i18n.t('backoffice.store.uiFontProfile', { f: profileFaceName() }) }}</option>
                    @for (font of fonts; track font.key) {
                      <option [value]="font.key">{{ font.label || i18n.t('backoffice.store.uiFontSystem') }}</option>
                    }
                  </select>
                  <span class="bo-hint">{{ i18n.t('backoffice.store.uiFontSync') }}</span>
                </div>
              </div>
            </app-fold>
          </div>
          <div class="bo-panel__foot">
            <button type="button" class="btn-primary" [disabled]="busy() || missingRequired()" (click)="save()">
              {{ i18n.t('common.save') }}
            </button>
          </div>
        </section>
      }

      <!-- Printed documents (docs/07 §4.3). Admin-owned and never replicated: the server keeps
           these off the till's StoreDto and saves them without bumping the Store version, so the
           group has its own load and its own Save rather than riding the form above.

           Laid out as cards (owner, 2026-10-06): every setting is one card in its category, the
           whole card opens that setting alone in the shared dialog, and the dialog's Save is the
           group's one PUT. Only the open editor can hold a change — Cancel puts the form back as it
           was when the editor opened — so a Save never carries an edit made somewhere unseen. -->
      @if (isAdmin() && docs(); as d) {
        <section class="docs" data-docs aria-labelledby="docs-title">
          <header class="docs__head">
            <h2 class="docs__title" id="docs-title">{{ i18n.t('store.docs.title') }}</h2>
            <p class="docs__lede">{{ i18n.t('store.docs.lede') }}</p>
          </header>
          @if (docsError() && !editing()) {
            <p class="notice danger" role="alert">{{ docsError() }}</p>
          }
          @if (docsSaved()) {
            <p class="notice ok" role="status">{{ i18n.t('common.saved') }}</p>
          }

          @for (group of docGroups(); track group.id) {
            <section class="docs-cat" [attr.aria-labelledby]="'docs-cat-' + group.id">
              <h3 class="docs-cat__title" [id]="'docs-cat-' + group.id">{{ i18n.t('store.docs.cat.' + group.id) }}</h3>
              <div class="docs-grid">
                @for (key of group.cards; track key) {
                  <button
                    type="button"
                    class="doc-card"
                    aria-haspopup="dialog"
                    [disabled]="docsBusy() || (key === 'addWording' && d.wordings.length >= maxWordings)"
                    (click)="openCard(key)"
                  >
                    <span class="doc-card__main">
                      <span class="doc-card__head">
                        <span class="doc-card__title">{{ cardTitle(key) }}</span>
                        @if (cardBadge(key); as b) {
                          <span class="bo-state" [class.bo-state--warn]="b.warn">{{ b.text }}</span>
                        }
                      </span>
                      <span class="doc-card__desc">
                        @for (part of cardDesc(key); track $index) {
                          <span dir="auto">{{ part }}</span>
                        }
                      </span>
                    </span>
                    <!-- U+203A is bidi-mirrored: the browser turns it to face the inline end in Arabic. -->
                    <span class="doc-card__chev" aria-hidden="true">›</span>
                  </button>
                }
              </div>
            </section>
          }
        </section>

        <app-dialog
          [open]="!!editing()"
          [size]="editing() === 'sample' ? 'lg' : 'md'"
          [heading]="editing() ? cardTitle(editing()!) : ''"
          (dismiss)="dismissEditor()"
        >
          @if (editing(); as key) {
            <div class="bo-form">
              @switch (key) {
                @case ('contact') {
                  <div class="bo-fields bo-fields--roomy">
                    <div class="bo-field">
                      <label for="sd-email">{{ i18n.t('store.email') }}</label>
                      <input id="sd-email" type="email" maxlength="200" [(ngModel)]="d.email" />
                    </div>
                    <div class="bo-field">
                      <label for="sd-website">{{ i18n.t('store.website') }}</label>
                      <input id="sd-website" maxlength="200" [(ngModel)]="d.website" />
                    </div>
                    <div class="bo-field bo-field--wide">
                      <label for="sd-bank">{{ i18n.t('store.bank') }}</label>
                      <textarea id="sd-bank" rows="2" maxlength="400" class="mono" [(ngModel)]="d.bankDetails"></textarea>
                      <span class="bo-hint">{{ i18n.t('store.bankHint') }}</span>
                    </div>
                  </div>
                }

                <!-- The logo at the head of every printed document (owner, 2026-09-30). Saved with
                     the rest of this group and spending none of the wording allowance. The hint names
                     the black-and-white cut because the dark original prints as a black block on a
                     mono printer, which is what made the owner's paper look the way it did. -->
                @case ('logo') {
                  <p class="bo-group__note">{{ i18n.t('logo.hint') }}</p>
                  <div class="logo-row">
                    <div class="logo-preview">
                      @if (d.logoUrl) {
                        @if (logoPreview(); as src) {
                          <img [src]="src" [attr.alt]="companyName()" />
                        }
                      } @else {
                        <span class="bo-soft">{{ i18n.t('logo.none') }}</span>
                      }
                    </div>
                    <div class="bo-field">
                      <div class="bo-actions bo-actions--start">
                        <!-- A real button opens the picker: a styled label is not reachable by keyboard. -->
                        <button
                          type="button"
                          class="btn-ghost"
                          [disabled]="docsBusy()"
                          aria-describedby="sd-logo-types"
                          (click)="logoFile.click()"
                        >
                          {{ i18n.t(d.logoUrl ? 'logo.replace' : 'logo.choose') }}
                        </button>
                        <input #logoFile type="file" hidden [accept]="logoTypes" (change)="pickLogo($event)" />
                        @if (d.logoUrl) {
                          <button type="button" class="btn-ghost" [disabled]="docsBusy()" (click)="removeLogo()">
                            {{ i18n.t('logo.remove') }}
                          </button>
                        }
                      </div>
                      <span class="bo-hint" id="sd-logo-types">{{ i18n.t('logo.types', { kb: logoMaxKb }) }}</span>
                      @if (logoProblem()) {
                        <span class="bo-error" role="alert">{{ logoProblem() }}</span>
                      }
                      @if (logoPending()) {
                        <span class="bo-hint">{{ i18n.t('logo.pending') }}</span>
                      }
                    </div>
                  </div>
                }

                <!-- The languages the title of every printed document is set in (owner, 2026-10-02:
                     the return note reads "BON DE RETOUR" with the Arabic beside it). The rest of the
                     sheet stays in the language chosen at print time; none ticked is that language alone. -->
                @case ('titleLangs') {
                  <p class="bo-group__note">{{ i18n.t('store.docs.titleLangs.note') }}</p>
                  <div class="bo-fields">
                    @for (l of i18n.langs(); track l.code) {
                      <div class="bo-check">
                        <input
                          [id]="'sd-title-' + l.code"
                          type="checkbox"
                          [checked]="d.titleLanguages.includes(l.code)"
                          (change)="toggleTitleLanguage(l.code)"
                        />
                        <label [for]="'sd-title-' + l.code">{{ l.label }}</label>
                      </div>
                    }
                  </div>
                }

                <!-- How an item is named on the rental papers (owner, 2026-10-02, answer 28: one name,
                     Arabic or French, the admin chooses — or both). Unset keeps what the papers have
                     always printed: the French name signed, the Arabic beneath. -->
                @case ('itemNames') {
                  <p class="bo-group__note">{{ i18n.t('store.docs.itemNames.note') }}</p>
                  <div class="bo-fields">
                    <div class="bo-field">
                      <label for="sd-item-name">{{ i18n.t('store.docs.itemNames.main') }}</label>
                      <select id="sd-item-name" [ngModel]="d.itemName" (ngModelChange)="setItemName($event)">
                        <option value="">{{ i18n.t('store.docs.itemNames.default') }}</option>
                        @for (l of i18n.langs(); track l.code) {
                          <option [value]="l.code">{{ l.label }}</option>
                        }
                      </select>
                    </div>
                    @if (d.itemName) {
                      <div class="bo-field">
                        <label for="sd-item-beneath">{{ i18n.t('store.docs.itemNames.beneath') }}</label>
                        <select id="sd-item-beneath" [ngModel]="d.itemNameBeneath" (ngModelChange)="setItemNameBeneath($event)">
                          <option value="">{{ i18n.t('store.docs.itemNames.none') }}</option>
                          @for (l of i18n.langs(); track l.code) {
                            @if (l.code !== d.itemName) {
                              <option [value]="l.code">{{ l.label }}</option>
                            }
                          }
                        </select>
                      </div>
                    }
                  </div>
                }

                <!-- The yearly allowance, and the number that sets it. It is the admin's own
                     discipline: he sets it, he can raise it, and what actually keeps a signed copy
                     readable is the history under each kind — the note says exactly that, so nobody
                     reads the limit as something done to him. -->
                @case ('limit') {
                  <p class="bo-group__note">{{ i18n.t('store.docs.limit.note') }}</p>
                  <div class="bo-fields">
                    <div class="bo-field">
                      <label for="sd-limit">{{ i18n.t('store.docs.limit.label') }}</label>
                      <input id="sd-limit" type="number" min="0" step="1" [(ngModel)]="d.changesPerYear" />
                      <span class="bo-hint">{{ i18n.t('store.docs.limit.hint') }}</span>
                    </div>
                  </div>
                }

                <!-- Something to start from, for a company whose own paragraphs do not exist yet.
                     It fills the boxes and saves nothing: he reads it here, edits it and saves it
                     himself, and only then does it cost one of the year's changes. -->
                @case ('sample') {
                  <p class="bo-group__note">{{ i18n.t('store.docs.sample.note') }}</p>
                  @if (!sampleFilled()) {
                    <p class="bo-meta">
                      <button type="button" class="btn-ghost" [disabled]="docsBusy()" (click)="fillWithSample()">
                        {{ i18n.t('store.docs.sample.fill') }}
                      </button>
                    </p>
                  } @else {
                    <p class="notice warn" role="status">{{ i18n.t('store.docs.sample.filled') }}</p>
                    @for (kind of kinds; track kind) {
                      <div class="bo-group">
                        <div class="sample-head">
                          <h3 class="sample-head__title">{{ cardTitle(kind) }}</h3>
                          @if (cardBadge(kind); as b) {
                            <span class="bo-state" [class.bo-state--warn]="b.warn">{{ b.text }}</span>
                          }
                        </div>
                        <ng-container *ngTemplateOutlet="textBoxes; context: { $implicit: kind }" />
                      </div>
                    }
                  }
                }

                @default {
                  <!-- One wording: a kind's, or a named contract wording's (owner, PinSay #302) with
                       its names first. -->
                  <p class="bo-group__note">{{ i18n.t('store.docs.hint') }}</p>
                  @if (wordingOf(key); as w) {
                    <div class="bo-fields">
                      @for (l of i18n.langs(); track l.code) {
                        <div class="bo-field">
                          <label [for]="'sd-' + w.key + '-name-' + l.code">
                            {{ i18n.t('store.docs.wordings.name', { lang: l.label }) }}
                          </label>
                          <input
                            [id]="'sd-' + w.key + '-name-' + l.code"
                            [maxlength]="nameMax"
                            [dir]="l.code === 'ar' ? 'rtl' : 'ltr'"
                            [(ngModel)]="w.names[l.code]"
                          />
                        </div>
                      }
                    </div>
                    <p class="bo-hint">{{ i18n.t('store.docs.wordings.nameHint') }}</p>
                  }
                  <ng-container *ngTemplateOutlet="wordingTexts; context: { $implicit: key }" />
                  @if (wordingOf(key)) {
                    <div class="bo-actions bo-actions--start">
                      <button type="button" class="btn-ghost" [disabled]="docsBusy()" (click)="removeWording(key)">
                        {{ i18n.t('store.docs.wordings.remove') }}
                      </button>
                    </div>
                    <p class="bo-hint">{{ i18n.t('store.docs.wordings.removeHint') }}</p>
                  }
                }
              }
              @if (docsError()) {
                <p class="notice danger" role="alert">{{ docsError() }}</p>
              }
            </div>
          }

          <!-- Cancel and Save stay in the dialog's own foot, in view however long the body runs, and
               the state of the edit is said in words beside them. -->
          <div dialogFoot class="docs-foot">
            @if (editorDirty()) {
              <span class="bo-state bo-state--warn docs-foot__state" role="status">{{ i18n.t('store.docs.editor.unsaved') }}</span>
            } @else {
              <span class="docs-foot__state docs-foot__state--clean">{{ i18n.t('store.docs.editor.clean') }}</span>
            }
            @if (editing(); as key) {
              @if (isContractWording(key)) {
                <button type="button" class="btn-ghost" aria-haspopup="dialog" [disabled]="docsBusy()" (click)="openPreview(key)">
                  {{ i18n.t('store.docs.preview.open') }}
                </button>
              }
            }
            <button type="button" class="btn-ghost" [disabled]="docsBusy()" (click)="dismissEditor()">
              {{ i18n.t('common.cancel') }}
            </button>
            <button type="button" class="btn-primary" [disabled]="docsBusy() || !editorDirty()" (click)="saveEditor()">
              {{ i18n.t('common.save') }}
            </button>
          </div>
        </app-dialog>

        <!-- The printed contract, over the editor (owner, 2026-10-08): the real paper with a sample
             contract and the wording as the editor holds it. It saves nothing, so it spends none of
             the year's changes; Close, Escape or the × go back to the editor and its draft. -->
        <app-dialog
          [open]="!!preview()"
          size="lg"
          [heading]="preview() ? i18n.t('store.docs.preview.title') : ''"
          (dismiss)="closePreview()"
        >
          @if (preview(); as p) {
            <div class="bo-form preview">
              <div class="preview-bar">
                <div class="bo-field">
                  <label for="sd-preview-lang">{{ i18n.t('print.lang') }}</label>
                  <select id="sd-preview-lang" [ngModel]="p.lang" (ngModelChange)="setPreviewLang($event)">
                    @for (l of i18n.langs(); track l.code) {
                      <option [value]="l.code">{{ l.label }}</option>
                    }
                  </select>
                </div>
                <p class="bo-meta preview-bar__what">
                  <strong dir="auto">{{ cardTitle(p.key) }}</strong>
                  @if (editorDirty()) {
                    <span class="bo-state bo-state--warn">{{ i18n.t('store.docs.editor.unsaved') }}</span>
                  }
                </p>
              </div>
              <p class="bo-callout" role="note">{{ i18n.t('store.docs.preview.note') }}</p>
              <!-- Its own chunk: the rental paper and its arithmetic download when a preview first
                   opens, not with the settings page. -->
              @defer (on immediate) {
                <app-contract-preview [documents]="p.documents" [wordingKey]="p.key" [lang]="p.lang" />
              } @loading {
                <p class="notice" role="status">{{ i18n.t('common.loading') }}</p>
              }
            </div>
          }
          <div dialogFoot class="docs-foot">
            <button type="button" class="btn-ghost" (click)="closePreview()">{{ i18n.t('common.close') }}</button>
          </div>
        </app-dialog>

        <!-- One wording's body, a kind's or a named contract wording's: what the year has used,
             the text per language, and the wordings it replaced. -->
        <ng-template #wordingTexts let-key>
          @if (allowanceFor(key); as a) {
            <p class="bo-meta">
              <span class="bo-state" [class.bo-state--warn]="a.remaining === 0">
                {{
                  a.remaining === 0
                    ? i18n.t('store.docs.allowance.none')
                    : i18n.t('store.docs.allowance.left', { n: a.remaining, limit: a.limit })
                }}
              </span>
              <span>
                {{
                  i18n.t('store.docs.allowance.used', {
                    n: a.used,
                    year: a.year,
                    date: i18n.date(a.resetsOnUtc)
                  })
                }}
              </span>
            </p>
          }
          <ng-container *ngTemplateOutlet="textBoxes; context: { $implicit: key }" />
          @if (historyFor(key).length) {
            <div class="bo-actions bo-actions--start">
              <button type="button" class="btn-ghost" (click)="toggleHistory(key)">
                {{
                  isHistoryOpen(key)
                    ? i18n.t('store.docs.history.hide')
                    : i18n.t('store.docs.history.show', { n: historyFor(key).length })
                }}
              </button>
            </div>
            @if (isHistoryOpen(key)) {
              @for (h of historyFor(key); track h.id) {
                <div class="bo-group__note">
                  <p class="bo-meta">
                    <span>{{ i18n.dateTime(h.changedAtUtc) }}</span>
                    @if (h.changedBy) {
                      <span>{{ h.changedBy }}</span>
                    }
                    <span>{{ i18n.t('store.docs.history.before') }}</span>
                  </p>
                  @for (v of previousOf(h); track v.lang) {
                    <p class="bo-hint" [dir]="v.lang === 'ar' ? 'rtl' : 'ltr'">
                      <strong>{{ langLabel(v.lang) }}</strong>
                      {{ v.text }}
                    </p>
                  }
                  @if (!previousOf(h).length) {
                    <p class="bo-hint">{{ i18n.t('store.docs.history.nothing') }}</p>
                  }
                </div>
              }
            }
          }
        </ng-template>

        <!-- The text of one wording, one box per enabled language. A contract wording offers its
             fields above the boxes (owner, 2026-10-08): each chip puts its token where the cursor
             was in the box last typed in, and the paper prints the contract's own value there. -->
        <ng-template #textBoxes let-key>
          @if (isContractWording(key)) {
            <div class="field-chips">
              <span class="field-chips__title" [id]="'sd-' + key + '-fields'">{{ i18n.t('store.docs.fields.title') }}</span>
              <!-- The mouse press is held back so the box keeps its focus and its caret. -->
              <div class="bo-chips" role="group" [attr.aria-labelledby]="'sd-' + key + '-fields'">
                @for (field of contractFields(); track field) {
                  <button
                    type="button"
                    [attr.title]="token(field)"
                    (mousedown)="$event.preventDefault()"
                    (click)="insertField(key, field)"
                  >
                    {{ fieldLabel(field) }}
                  </button>
                }
              </div>
              <p class="bo-hint">{{ i18n.t('store.docs.fields.hint') }} {{ i18n.t('store.docs.fields.itemsHint') }}</p>
              @if (fieldProblem()) {
                <span class="bo-error" role="alert">{{ fieldProblem() }}</span>
              }
            </div>
          }
          <div class="text-boxes">
            @for (l of i18n.langs(); track l.code) {
              <div class="bo-field">
                <label [for]="'sd-' + key + '-' + l.code">{{ l.label }}</label>
                <textarea
                  [id]="'sd-' + key + '-' + l.code"
                  rows="4"
                  [maxlength]="maxText"
                  [dir]="l.code === 'ar' ? 'rtl' : 'ltr'"
                  [(ngModel)]="d.texts[key][l.code]"
                  (focus)="noteBox(key, l.code)"
                ></textarea>
              </div>
            }
          </div>
        </ng-template>
      }

      <section class="bo-panel">
        <div class="bo-panel__head">
          <h2 class="bo-panel__title">{{ i18n.t('backoffice.store.countryProfile') }}</h2>
          <p class="bo-panel__note">{{ i18n.t('backoffice.store.countryProfileHint') }}</p>
        </div>
        <div class="bo-scroll">
          <table class="bo-data">
            <tbody>
              <tr>
                <th scope="row">{{ i18n.t('backoffice.store.country') }}</th>
                <td class="mono" [attr.data-label]="i18n.t('backoffice.store.country')">{{ profile().countryCode }}</td>
              </tr>
              <tr>
                <th scope="row">{{ i18n.t('backoffice.store.currency') }}</th>
                <td [attr.data-label]="i18n.t('backoffice.store.currency')">
                  <span class="mono">{{ profile().currencyCode }}</span>
                  <span class="mono">{{ i18n.money(1234.5) }}</span>
                </td>
              </tr>
              <tr>
                <th scope="row">{{ i18n.t('backoffice.store.taxRates') }}</th>
                <td [attr.data-label]="i18n.t('backoffice.store.taxRates')">
                  @for (rate of rateLabels(); track rate) {
                    <span class="bo-state">{{ rate }}</span>
                  }
                </td>
              </tr>
              <tr>
                <th scope="row">{{ i18n.t('backoffice.store.cashRounding') }}</th>
                <td [attr.data-label]="i18n.t('backoffice.store.cashRounding')">{{ roundingLabel() }}</td>
              </tr>
              <tr>
                <th scope="row">{{ i18n.t('backoffice.store.pricing') }}</th>
                <td [attr.data-label]="i18n.t('backoffice.store.pricing')">
                  {{ i18n.t(profile().pricesIncludeTax ? 'backoffice.store.pricesIncludeTax' : 'backoffice.store.pricesExcludeTax') }}
                </td>
              </tr>
              <tr>
                <th scope="row">{{ i18n.t('backoffice.store.timeZone') }}</th>
                <td class="mono" [attr.data-label]="i18n.t('backoffice.store.timeZone')">{{ profile().timeZone }}</td>
              </tr>
            </tbody>
          </table>
        </div>
      </section>
    </div>
  `,
  styleUrls: ['./backoffice-ui.scss'],
  styles: [
    `
      /* A read-only profile is a list of facts, so the label is the row header. */
      .bo-data th[scope='row'] {
        width: 14rem;
        background: var(--surface-2);
        color: var(--text);
        text-transform: none;
        letter-spacing: 0;
        font-size: 0.82rem;
        white-space: normal;
      }
      .bo-data td {
        overflow-wrap: anywhere;
      }
      /* The page holds one width and sits in the middle of a wide workspace (owner, 2026-10-06). */
      .store-page {
        inline-size: 100%;
        max-inline-size: 80rem;
        margin-inline: auto;
      }

      /* --- documents: categories of cards, each card opening one editor ---------------- */
      .docs {
        display: grid;
        gap: var(--sp-6);
        margin-block-start: var(--sp-5);
      }
      .docs__head {
        display: grid;
        gap: var(--sp-1);
      }
      .docs__title {
        margin: 0;
        font-size: var(--fs-page);
        font-weight: 600;
        line-height: var(--lh-tight);
      }
      .docs__lede {
        margin: 0;
        max-inline-size: var(--read-max);
        font-size: var(--fs-sm);
        color: var(--muted);
      }
      .docs-cat {
        display: grid;
        gap: var(--sp-3);
      }
      .docs-cat__title {
        margin: 0;
        font-size: var(--fs-section);
        font-weight: 600;
        line-height: var(--lh-tight);
      }
      .docs-grid {
        display: grid;
        grid-template-columns: repeat(2, minmax(0, 1fr));
        gap: var(--sp-3);
      }
      /* A card is one button: the whole of it opens the editor. Hairline and surface only — it
         sits on the page like a panel does, and its hover is the system's recessed fill. */
      .doc-card {
        display: flex;
        align-items: center;
        gap: var(--sp-4);
        inline-size: 100%;
        min-inline-size: 0;
        min-block-size: 4.25rem;
        padding: var(--sp-4) var(--sp-5);
        border: 1px solid var(--border);
        border-radius: var(--radius);
        background: var(--surface);
        color: var(--text);
        font: inherit;
        font-weight: inherit;
        text-align: start;
        cursor: pointer;
        transition: background var(--t-fast) ease, border-color var(--t-fast) ease;
      }
      .doc-card:hover:not(:disabled) {
        background: var(--surface-2);
        border-color: var(--border-strong);
      }
      .doc-card__main {
        flex: 1 1 auto;
        min-inline-size: 0;
        display: grid;
        gap: var(--sp-1);
      }
      .doc-card__head {
        display: flex;
        flex-wrap: wrap;
        align-items: center;
        gap: var(--sp-1) var(--sp-3);
      }
      .doc-card__title {
        font-size: var(--fs-card);
        font-weight: 600;
        line-height: var(--lh-tight);
      }
      /* One line: what does not fit is cut, never wrapped into a taller card. */
      .doc-card__desc {
        display: flex;
        gap: 0 var(--sp-3);
        min-inline-size: 0;
        overflow: hidden;
        font-size: var(--fs-sm);
        color: var(--muted);
      }
      .doc-card__desc > span {
        min-inline-size: 0;
        overflow: hidden;
        white-space: nowrap;
        text-overflow: ellipsis;
      }
      .doc-card__chev {
        flex: none;
        font-size: var(--fs-lg);
        line-height: 1;
        color: var(--muted);
      }
      /* The dialog's foot: the state of the edit in words at the start, the two buttons at the end. */
      .docs-foot {
        display: contents;
      }
      .docs-foot__state {
        margin-inline-end: auto;
        align-self: center;
      }
      .docs-foot__state--clean {
        font-size: var(--fs-meta);
        color: var(--muted);
      }
      /* One box per language, stacked: no label row reserved for a second line it never needs. */
      .text-boxes {
        display: grid;
        gap: var(--sp-4);
      }
      /* The fields a contract wording may carry: named like a field label, the chips under it, the
         one-line hint beneath. */
      .field-chips {
        display: grid;
        gap: var(--sp-2);
      }
      .field-chips__title {
        font-size: var(--fs-sm);
        font-weight: 500;
        color: var(--text);
      }
      .field-chips .bo-hint {
        margin: 0;
      }
      /* The preview's own bar: the document language on one side, which wording it is (and that
         it is not saved) in words beside it. */
      .preview {
        gap: var(--sp-4);
      }
      .preview-bar {
        display: flex;
        flex-wrap: wrap;
        align-items: flex-end;
        gap: var(--sp-3) var(--sp-6);
      }
      .preview-bar .bo-field {
        min-inline-size: 12rem;
      }
      .preview-bar__what {
        align-items: center;
        padding-block-end: var(--sp-2);
      }
      .sample-head {
        display: flex;
        flex-wrap: wrap;
        align-items: center;
        gap: var(--sp-1) var(--sp-3);
      }
      .sample-head__title {
        margin: 0;
        font-size: var(--fs-card);
        font-weight: 600;
      }
      @media (max-width: 719px) {
        .docs-grid {
          grid-template-columns: minmax(0, 1fr);
        }
        .doc-card {
          padding: var(--sp-4);
        }
      }
      /* The logo as it will print: on white, at about its paper height, beside its controls. */
      .logo-row {
        display: flex;
        flex-wrap: wrap;
        align-items: flex-start;
        gap: 1rem;
      }
      .logo-preview {
        display: flex;
        align-items: center;
        justify-content: center;
        min-inline-size: 8rem;
        block-size: 5rem;
        padding: 0.5rem;
        background: var(--surface);
        border: 1px solid var(--border);
        border-radius: var(--radius);
      }
      .logo-preview img {
        block-size: 100%;
        max-inline-size: 12rem;
        object-fit: contain;
      }
      @media (max-width: 719px) {
        .bo-data th[scope='row'] {
          display: block;
          width: auto;
          background: transparent;
          padding: 0 0 0.1rem;
          border: 0;
          color: var(--bo-ink-2);
          font-weight: 600;
        }
        .bo-data td[data-label] {
          display: block;
        }
        .bo-data td[data-label]::before {
          content: none;
        }
      }
    `,
  ],
})
export class BackofficeStoreComponent implements OnInit {
  private readonly admin = inject(AdminService);
  private readonly documentsApi = inject(StoreDocumentsApi);
  private readonly pos = inject(PosService);
  private readonly auth = inject(AuthService);
  readonly i18n = inject(I18nService);

  // Printed documents: their own state, their own save (see the template note).
  readonly kinds = DOCUMENT_TEXT_KINDS;
  readonly maxText = DOCUMENT_TEXT_MAX;
  /** The printed documents' texts are documents.manage's (the Admin's by role); read from the role on a session from before keys. */
  readonly isAdmin = computed(() => mayDo(this.auth.currentUser(), 'documents.manage'));
  readonly docs = signal<DocumentsForm | null>(null);
  readonly docsBusy = signal(false);
  readonly docsError = signal<string | null>(null);
  readonly docsSaved = signal(false);
  /** What the server counted for this year, per kind and per named wording — read before he types, never guessed here. */
  readonly allowances = signal<Partial<Record<DocumentTextKey, DocumentTextAllowance>>>({});
  /** The superseded wordings, newest first, grouped by kind. */
  readonly history = signal<Partial<Record<DocumentTextKey, DocumentTextChange[]>>>({});
  private readonly openHistory = signal<string[]>([]);
  readonly maxWordings = MAX_CONTRACT_WORDINGS;
  readonly nameMax = CONTRACT_WORDING_NAME_MAX;
  /** Wordings added since the last save: opened, and said "not saved yet" beside their heading. */
  private readonly fresh = signal<string[]>([]);

  // The documents page as cards (owner, 2026-10-06): each card opens one setting in the dialog.
  private readonly confirm = inject(ConfirmService);
  /** The card whose editor is open: a setting's name, or a wording's key (a kind or a named one). */
  readonly editing = signal<string | null>(null);
  /** The sample has been put into the boxes of the open sample editor, to be read before Save. */
  readonly sampleFilled = signal(false);
  /** The form as it stood when the editor opened: what Cancel puts back, and what "unsaved" is measured against. */
  private opened: { form: string; fresh: string[]; logoPending: boolean } | null = null;
  private static readonly SETTINGS = new Set(['contact', 'logo', 'titleLangs', 'itemNames', 'limit', 'sample', 'addWording']);

  // A contract wording as a template (owner, 2026-10-08): fields inside the text, and a preview of
  // the paper it prints. Neither saves anything; both work on the draft in the open editor.
  /** The fields a contract wording may carry, offered as chips above its boxes. */
  /** The fixed fields and one per fiscal identifier of the company's country profile. */
  readonly contractFields = computed(() => contractFieldsFor(this.profile()));
  /** A field that would not fit in the text any more, said under the chips. */
  readonly fieldProblem = signal<string | null>(null);
  /** The box last typed in, so a field goes where its cursor is. */
  private lastBox: { key: string; lang: string } | null = null;
  /**
   * The open preview: the wording, the document language, and the settings as the editor held
   * them when it opened — a snapshot, so switching the language redraws the same draft.
   */
  readonly preview = signal<{ key: DocumentTextKey; lang: Lang; documents: StoreDocumentsDto } | null>(null);

  /**
   * The settings that exist, grouped for reading (invent nothing: every card is a setting the PUT
   * already carries). The named contract wordings follow the standard one they stand beside.
   */
  docGroups(): { id: string; cards: string[] }[] {
    const wordings = this.docs()?.wordings.map((w) => w.key) ?? [];
    return [
      { id: 'identity', cards: ['contact', 'logo', 'titleLangs', 'itemNames'] },
      { id: 'wording', cards: ['limit', 'sample'] },
      { id: 'financial', cards: ['invoice', 'creditNote', 'receiptVoucher', 'statement'] },
      { id: 'rental', cards: ['rentalContract', ...wordings, 'issueNote', 'returnNote', 'addWording'] },
      { id: 'payments', cards: ['paymentVoucher', 'payslip'] },
    ];
  }

  /** The named contract wording behind a key, or null for a kind and for the settings. */
  wordingOf(key: string): ContractWording | null {
    return this.docs()?.wordings.find((w) => w.key === key) ?? null;
  }

  cardTitle(key: string): string {
    switch (key) {
      case 'contact':
        return this.i18n.t('common.group.contact');
      case 'logo':
        return this.i18n.t('logo.title');
      case 'titleLangs':
        return this.i18n.t('store.docs.titleLangs.title');
      case 'itemNames':
        return this.i18n.t('store.docs.itemNames.title');
      case 'limit':
        return this.i18n.t('store.docs.limit.title');
      case 'sample':
        return this.i18n.t('store.docs.sample.title');
      case 'addWording':
        return this.i18n.t('store.docs.wordings.add');
    }
    const wording = this.wordingOf(key);
    if (wording) return this.i18n.t('store.docs.wordings.heading', { name: this.wordingName(wording) });
    if (key === 'rentalContract' && this.docs()?.wordings.length) return this.i18n.t('store.docs.wordings.standard');
    return this.i18n.t('store.docs.kind.' + key);
  }

  /**
   * The card's one muted line: what the setting holds now where that can be said briefly (the
   * wording's opening words, the languages chosen), else what the setting is for. Parts, not a
   * joined sentence, so no separator sits between an Arabic and a Latin run.
   */
  cardDesc(key: string): string[] {
    const d = this.docs();
    if (!d) return [];
    const label = (code: string) => this.langLabel(code);
    switch (key) {
      case 'contact':
        return [this.i18n.t('store.docs.card.contact')];
      case 'logo':
        return [this.i18n.t(d.logoUrl ? 'store.docs.card.logoSet' : 'logo.none')];
      case 'titleLangs':
        return d.titleLanguages.length ? d.titleLanguages.map(label) : [this.i18n.t('store.docs.card.titleLangsNone')];
      case 'itemNames':
        return d.itemName
          ? [label(d.itemName), ...(d.itemNameBeneath ? [label(d.itemNameBeneath)] : [])]
          : [this.i18n.t('store.docs.itemNames.default')];
      case 'limit':
        return [this.i18n.t('store.docs.card.limit', { n: d.changesPerYear ?? 0 })];
      case 'sample':
        return [this.i18n.t('store.docs.card.sample')];
      case 'addWording':
        return [
          d.wordings.length >= this.maxWordings
            ? this.i18n.t('store.docs.wordings.max', { n: this.maxWordings })
            : this.i18n.t('store.docs.card.addWording'),
        ];
    }
    const texts = d.texts[key] ?? {};
    const text = (this.i18n.text(texts) || Object.values(texts).find((t) => t?.trim()) || '').trim();
    return [text ? text.split(/\r?\n/)[0] : this.i18n.t('store.docs.card.noText')];
  }

  /**
   * The state a card says beside its title, in words: a wording's allowance for the year, or
   * "not saved yet" for one just added. Settings that spend no allowance carry none.
   */
  cardBadge(key: string): { text: string; warn: boolean } | null {
    if (BackofficeStoreComponent.SETTINGS.has(key)) return null;
    if (this.isFresh(key)) return { text: this.i18n.t('store.docs.wordings.notSaved'), warn: true };
    const a = this.allowanceFor(key);
    if (!a) return null;
    return a.remaining === 0
      ? { text: this.i18n.t('store.docs.allowance.none'), warn: true }
      : { text: this.i18n.t('store.docs.allowance.left', { n: a.remaining, limit: a.limit }), warn: false };
  }

  /** Opens one card's editor; "add a wording" adds it first, so Cancel takes it away again. */
  openCard(key: string): void {
    const d = this.docs();
    if (!d || this.docsBusy()) return;
    this.opened = { form: JSON.stringify(d), fresh: [...this.fresh()], logoPending: this.logoPending() };
    this.docsError.set(null);
    this.docsSaved.set(false);
    this.logoProblem.set(null);
    this.fieldProblem.set(null);
    this.sampleFilled.set(false);
    if (key === 'addWording') {
      if (d.wordings.length >= MAX_CONTRACT_WORDINGS) return;
      this.addWording();
      key = this.docs()!.wordings[this.docs()!.wordings.length - 1].key;
    }
    this.editing.set(key);
  }

  /** Something in the open editor differs from what it opened on. */
  editorDirty(): boolean {
    const d = this.docs();
    return !!this.opened && !!d && JSON.stringify(d) !== this.opened.form;
  }

  /** Escape, the backdrop, the × or Cancel: asks first when something typed would be lost. */
  async dismissEditor(): Promise<void> {
    if (this.docsBusy()) return;
    if (this.editorDirty()) {
      const discard = await this.confirm.ask({
        title: this.i18n.t('ui.discard.title'),
        message: this.i18n.t('ui.discard.message'),
        confirmLabel: this.i18n.t('ui.discard.ok'),
        cancelLabel: this.i18n.t('ui.discard.keep'),
        danger: true,
      });
      if (!discard) return;
    }
    this.closeEditor(true);
  }

  /** The editor's Save is the group's one Save; the editor closes once the server has taken it. */
  async saveEditor(): Promise<void> {
    if (!(await this.confirm.ask(saveRequest(this.i18n)))) return;
    if (await this.saveDocuments()) this.closeEditor(false);
  }

  private closeEditor(revert: boolean): void {
    if (revert && this.opened) {
      this.docs.set(JSON.parse(this.opened.form) as DocumentsForm);
      this.fresh.set(this.opened.fresh);
      this.logoPending.set(this.opened.logoPending);
      this.docsError.set(null);
    }
    this.opened = null;
    this.logoProblem.set(null);
    this.fieldProblem.set(null);
    this.preview.set(null);
    this.sampleFilled.set(false);
    this.editing.set(null);
  }

  /** The standard rental-contract wording or a named one: the texts that carry fields and preview. */
  isContractWording(key: string): boolean {
    return key === 'rentalContract' || key.startsWith('rentalContract.');
  }

  token(field: string): string {
    return fieldToken(field);
  }

  /** A fixed field's own name, or "NIF de l'entreprise" for one of the company's identifiers. */
  fieldLabel(field: string): string {
    if ((CONTRACT_FIELDS as readonly string[]).includes(field)) return this.i18n.t('store.docs.fields.' + field);
    const id = this.profile().fiscalIds.find((d) => companyIdField(d.key) === field);
    return id ? this.i18n.t('store.docs.fields.companyId', { code: id.code }) : field;
  }

  noteBox(key: string, lang: string): void {
    this.lastBox = { key, lang };
  }

  /**
   * The language a chip writes into and a preview opens in: the box last typed in for this
   * wording, else the reader's own language, else the company's first.
   */
  private boxLang(key: string): Lang {
    const codes = this.i18n.langs().map((l) => l.code);
    if (this.lastBox?.key === key && codes.includes(this.lastBox.lang)) return this.lastBox.lang;
    const own = this.i18n.lang();
    return codes.includes(own) ? own : (codes[0] ?? own);
  }

  /**
   * Puts a field's token into the wording where the cursor was — over the selection, if there is
   * one — in the box last typed in; at the end of the reader's own language box when none was.
   * It is the same write the box's own `ngModel` makes, so the edit is unsaved like anything typed
   * and Cancel takes it away again. A field the text has no room left for is refused in words.
   */
  insertField(key: string, field: string): void {
    const form = this.docs();
    if (!form) return;
    const lang = this.boxLang(key);
    const texts = (form.texts[key] ??= {});
    const text = texts[lang] ?? '';
    const box = document.getElementById(`sd-${key}-${lang}`);
    const area = box instanceof HTMLTextAreaElement ? box : null;
    const typedIn = this.lastBox?.key === key && this.lastBox.lang === lang;
    const next = insertToken(
      text,
      typedIn ? area?.selectionStart : text.length,
      typedIn ? area?.selectionEnd : text.length,
      fieldToken(field),
      DOCUMENT_TEXT_MAX,
    );
    if (!next) {
      this.fieldProblem.set(this.i18n.t('store.docs.fields.tooLong', { n: DOCUMENT_TEXT_MAX }));
      return;
    }
    this.fieldProblem.set(null);
    texts[lang] = next.text;
    if (area) {
      area.value = next.text;
      area.focus();
      area.setSelectionRange(next.caret, next.caret);
    }
    this.lastBox = { key, lang };
  }

  /**
   * The printed-document settings as the open editor holds them, for the preview: the texts as the
   * Save would send them (trimmed, blanks dropped), and the previewed wording always present — a
   * named wording with no text yet previews with no terms, never with the standard ones.
   */
  draftDocuments(key: DocumentTextKey): StoreDocumentsDto | null {
    const form = this.docs();
    if (!form) return null;
    const documentTexts: StoreDocumentsDto['documentTexts'] = {};
    for (const [kind, byLang] of Object.entries(form.texts)) {
      const clean: Record<string, string> = {};
      for (const [lang, text] of Object.entries(byLang ?? {})) if (text?.trim()) clean[lang] = text.trim();
      if (Object.keys(clean).length || kind === key) documentTexts[kind as DocumentTextKey] = clean;
    }
    return {
      documentTexts,
      email: form.email.trim() || null,
      website: form.website.trim() || null,
      bankDetails: form.bankDetails.trim() || null,
      changesPerYear: form.changesPerYear,
      logoUrl: form.logoUrl,
      titleLanguages: [...form.titleLanguages],
      itemNameLanguages: form.itemName ? [form.itemName, ...(form.itemNameBeneath ? [form.itemNameBeneath] : [])] : [],
      contractWordings: form.wordings.map((w) => ({ key: w.key, names: { ...w.names } })),
    };
  }

  /** Opens the printed contract over the editor, on the draft as it stands; nothing is sent. */
  openPreview(key: string): void {
    if (!this.isContractWording(key)) return;
    const documents = this.draftDocuments(key as DocumentTextKey);
    if (!documents) return;
    this.preview.set({ key: key as DocumentTextKey, lang: this.boxLang(key), documents });
  }

  setPreviewLang(lang: Lang): void {
    this.preview.update((p) => (p ? { ...p, lang } : p));
  }

  /** Back to the editor, its draft untouched. */
  closePreview(): void {
    this.preview.set(null);
  }

  // The logo. Checked here as well as on the server, so a file that would be refused is said in
  // the reader's language before the save; the server's sentence stays the fallback.
  readonly logoTypes = LOGO_TYPES.join(',');
  readonly logoMaxKb = LOGO_MAX_BYTES / 1024;
  readonly logoProblem = signal<string | null>(null);
  /** A logo picked or removed and not saved yet — said in words, beside the Save it waits for. */
  readonly logoPending = signal(false);
  /** What the preview draws: a picked file as it is, the saved logo fetched with the token. */
  readonly logoPreview = signal<string | null>(null);
  private readonly images = inject(SecureImageService);
  // Keyed on the link alone, so editing the wording beside it does not refetch or flicker it.
  private readonly logoLink = computed(() => this.docs()?.logoUrl ?? null);
  private readonly previewLogo = effect(() => {
    const link = this.logoLink();
    this.logoPreview.set(null);
    void this.images.src(link).then((src) => {
      if (this.logoLink() === link) this.logoPreview.set(src);
    });
  });
  /** The preview's alt text: the name the header prints beside it. */
  readonly companyName = computed(() => {
    const f = this.form();
    return f ? f.nameFr || f.nameAr || f.nameEn : '';
  });

  readonly busy = signal(false);
  readonly error = signal<string | null>(null);
  readonly saved = signal(false);
  readonly form = signal<UpsertStoreRequest | null>(null);
  readonly profile = inject(RegionalService).settings;

  // The interface face. Kept beside the form rather than in it because the control has one
  // value the request does not — the empty string, which means "no company face, follow the
  // country profile" and is sent as an explicit null.
  readonly fonts = UI_FONTS;
  readonly uiFont = signal<string>('');
  /** The chosen face, or the profile's when the company follows it; empty means the built-in stack. */
  readonly facePreview = computed(() => uiFontStack(this.uiFont() || this.profile().uiFont));
  /** What following the profile actually gives you, named so the choice is not a guess. */
  readonly profileFaceName = computed(() => {
    const key = (this.profile().uiFont ?? '').trim().toLowerCase() || SYSTEM_FONT;
    return UI_FONTS.find((f) => f.key === key)?.label || this.i18n.t('backoffice.store.uiFontSystem');
  });
  /**
   * One label per rate, kept as a list rather than joined into a sentence: a written
   * separator between "19%" and an Arabic rate name lands on the wrong side of the run, and
   * each rate is its own fact anyway.
   */
  readonly rateLabels = computed(() =>
    this.profile().taxRates.map((r) => (this.i18n.rateLabel(r) ? `${r}% ${this.i18n.rateLabel(r)}` : `${r}%`)),
  );
  readonly roundingLabel = computed(() => {
    const { cashRoundingIncrement: step, cashRoundingMode: mode } = this.profile();
    if (!step) return this.i18n.t('backoffice.store.roundingOff');
    return `${this.i18n.money(step)} ${this.i18n.t('backoffice.store.rounding' + mode)}`;
  });

  async ngOnInit(): Promise<void> {
    this.busy.set(true);
    try {
      const store = await this.admin.getStore();
      this.form.set({
        nameAr: store.nameAr,
        nameFr: store.nameFr,
        nameEn: store.nameEn,
        address: store.addressFr ?? store.address,
        addressAr: store.addressAr ?? '',
        addressEn: store.addressEn ?? '',
        phone: store.phone,
        fiscalIds: { ...store.fiscalIds },
        receiptFooter: store.receiptFooterFr ?? store.receiptFooter,
        receiptFooterAr: store.receiptFooterAr ?? '',
        receiptFooterEn: store.receiptFooterEn ?? '',
        requireCustomerEmail: store.requireCustomerEmail,
        requireCustomerBirthDate: store.requireCustomerBirthDate,
        requireCustomerPhone: store.requireCustomerPhone,
        requireCustomerAddress: store.requireCustomerAddress,
        requireCustomerTaxId: store.requireCustomerTaxId,
        loyaltyEnabled: store.loyaltyEnabled ?? false,
        loyaltyPointsPerUnit: store.loyaltyPointsPerUnit ?? 0,
        loyaltyPointValue: store.loyaltyPointValue ?? 0,
        loyaltyMinRedeemPoints: store.loyaltyMinRedeemPoints ?? 0,
      });
      this.uiFont.set(store.uiFont ?? '');
    } catch (e) {
      this.error.set(adminErrorMessage(e));
    } finally {
      this.busy.set(false);
    }
    if (this.isAdmin()) await this.loadDocuments();
  }

  /**
   * Fills the form with wording to start from, and saves nothing.
   *
   * <p>He reads it, edits it, and saves it himself — at which point it is his text on the Store
   * row like any other and costs one of the year's changes. Seeding it silently would put clauses
   * this code invented onto a customer's contract with nobody having looked; every sample carries
   * a marker line saying what it is, and the line is part of the text, so it prints too.</p>
   */
  async fillWithSample(): Promise<void> {
    const form = this.docs();
    if (!form || this.docsBusy()) return;
    this.docsBusy.set(true);
    this.docsError.set(null);
    this.docsSaved.set(false);
    try {
      const dto = await this.documentsApi.sample();
      // The named wordings keep their own text: the sample only knows the kinds.
      const texts: Record<string, Record<string, string>> = { ...form.texts };
      // The company's enabled languages only: a text in a language nobody reads is refused by
      // the server anyway, and would sit in the form looking like work someone did.
      const enabled = new Set(this.i18n.langs().map((l) => l.code));
      for (const kind of DOCUMENT_TEXT_KINDS) {
        const sample = dto.documentTexts[kind] ?? {};
        texts[kind] = { ...form.texts[kind] };
        for (const [lang, text] of Object.entries(sample)) if (enabled.has(lang)) texts[kind][lang] = text;
      }
      this.docs.set({
        ...form,
        texts,
        email: form.email || (dto.email ?? ''),
        website: form.website || (dto.website ?? ''),
        bankDetails: form.bankDetails || (dto.bankDetails ?? ''),
      });
      this.sampleFilled.set(true);
    } catch (e) {
      this.docsError.set(adminErrorMessage(e));
    } finally {
      this.docsBusy.set(false);
    }
  }

  private async loadDocuments(): Promise<void> {
    try {
      const dto = await this.documentsApi.get();
      const wordings = (dto.contractWordings ?? []).map((w) => ({ key: w.key, names: { ...w.names } }));
      const texts: Record<string, Record<string, string>> = {};
      for (const key of [...DOCUMENT_TEXT_KINDS, ...wordings.map((w) => w.key)]) {
        texts[key] = { ...(dto.documentTexts[key as DocumentTextKey] ?? {}) };
      }
      this.fresh.set([]);
      this.docs.set({
        texts,
        wordings,
        email: dto.email ?? '',
        website: dto.website ?? '',
        bankDetails: dto.bankDetails ?? '',
        changesPerYear: dto.changesPerYear,
        logoUrl: dto.logoUrl ?? null,
        titleLanguages: [...(dto.titleLanguages ?? [])],
        itemName: dto.itemNameLanguages?.[0] ?? '',
        itemNameBeneath: dto.itemNameLanguages?.[1] ?? '',
      });
      this.applyAllowances(dto);
    } catch (e) {
      // An older server without the route: the group simply does not appear.
      this.docsError.set(adminErrorMessage(e));
      return;
    }
    await this.loadHistory();
  }

  /** The server counts; the screen only groups what it sent. */
  private applyAllowances(dto: StoreDocumentsDto): void {
    const byKind: Partial<Record<DocumentTextKey, DocumentTextAllowance>> = {};
    for (const allowance of dto.allowances ?? []) byKind[allowance.kind] = allowance;
    this.allowances.set(byKind);
  }

  /**
   * Its own call and its own failure: a history that will not load must not take the form with it.
   * The wording in force is on the form above; these are the ones it replaced.
   */
  private async loadHistory(): Promise<void> {
    try {
      const rows = await this.documentsApi.history();
      const byKind: Partial<Record<DocumentTextKey, DocumentTextChange[]>> = {};
      for (const row of rows) (byKind[row.kind] ??= []).push(row);
      this.history.set(byKind);
    } catch {
      this.history.set({});
    }
  }

  allowanceFor(kind: string): DocumentTextAllowance | null {
    return this.allowances()[kind as DocumentTextKey] ?? null;
  }

  historyFor(kind: string): DocumentTextChange[] {
    return this.history()[kind as DocumentTextKey] ?? [];
  }

  isHistoryOpen(kind: string): boolean {
    return this.openHistory().includes(kind);
  }

  toggleHistory(kind: string): void {
    this.openHistory.update((open) => (open.includes(kind) ? open.filter((k) => k !== kind) : [...open, kind]));
  }

  /** One superseded wording per language, in the order the interface lists its languages. */
  previousOf(change: DocumentTextChange): { lang: string; text: string }[] {
    return Object.entries(change.previousTexts ?? {}).map(([lang, text]) => ({ lang, text }));
  }

  isFresh(key: string): boolean {
    return this.fresh().includes(key);
  }

  /** The name the heading says: the reader's language first, as the contract form shows it. */
  wordingName(wording: ContractWording): string {
    return this.i18n.text(wording.names) || this.i18n.t('store.docs.wordings.unnamed');
  }

  /**
   * A new named wording, opened for its name and its text. Nothing is sent until Save, where it
   * costs its own first change — never the standard contract's.
   */
  addWording(): void {
    const d = this.docs();
    if (!d || d.wordings.length >= MAX_CONTRACT_WORDINGS) return;
    const key = newWordingKey(d.wordings.map((w) => w.key));
    this.fresh.update((f) => [...f, key]);
    this.docs.set({ ...d, wordings: [...d.wordings, { key, names: {} }], texts: { ...d.texts, [key]: {} } });
  }

  /**
   * Off the list at the next Save. The server refuses it while a contract prints the wording, and
   * keeps its last text in the history otherwise, so nothing is lost by trying.
   */
  removeWording(key: string): void {
    this.fresh.update((f) => f.filter((k) => k !== key));
    this.docs.update((d) => {
      if (!d) return d;
      const texts = { ...d.texts };
      delete texts[key];
      return { ...d, wordings: d.wordings.filter((w) => w.key !== key), texts };
    });
  }

  /** The name's language; the language beneath it can never be the same one. */
  setItemName(code: string): void {
    this.docs.update((d) => (d ? { ...d, itemName: code, itemNameBeneath: d.itemNameBeneath === code || !code ? '' : d.itemNameBeneath } : d));
  }

  setItemNameBeneath(code: string): void {
    this.docs.update((d) => (d ? { ...d, itemNameBeneath: code } : d));
  }

  /** Ticks or unticks one title language, keeping the order the interface lists its languages in. */
  toggleTitleLanguage(code: string): void {
    this.docs.update((d) => {
      if (!d) return d;
      const on = d.titleLanguages.includes(code)
        ? d.titleLanguages.filter((c) => c !== code)
        : [...d.titleLanguages, code];
      const order = this.i18n.langs().map((l) => l.code);
      return { ...d, titleLanguages: order.filter((c) => on.includes(c)) };
    });
  }

  /** The language's own name when the interface knows it, else the code as stored. */
  langLabel(code: string): string {
    return this.i18n.langs().find((l) => l.code === code)?.label ?? code;
  }

  /** Sends the group whole, as it always has. True once the server has taken it. */
  async saveDocuments(): Promise<boolean> {
    const form = this.docs();
    if (!form) return false;
    // Empty texts are dropped here as well as on the server, so the payload says what is set.
    const documentTexts: StoreDocumentsDto['documentTexts'] = {};
    const wordingKeys = form.wordings.map((w) => w.key);
    for (const kind of [...DOCUMENT_TEXT_KINDS, ...wordingKeys] as DocumentTextKey[]) {
      const byLang = Object.fromEntries(Object.entries(form.texts[kind] ?? {}).filter(([, text]) => text?.trim()));
      if (Object.keys(byLang).length) documentTexts[kind] = byLang;
    }
    // The whole list, names trimmed and blanks dropped; the server says in words what is missing.
    const contractWordings = form.wordings.map((w) => ({
      key: w.key,
      names: Object.fromEntries(
        Object.entries(w.names)
          .map(([lang, name]) => [lang, (name ?? '').trim()])
          .filter(([, name]) => name),
      ),
    }));
    // A cleared number field reads as null, and 0 is a real setting meaning "frozen" — so an
    // unreadable limit is said out loud rather than quietly sent as zero and freezing the wording.
    //
    // The emptiness has to be caught *before* the coercion, not after it: Number(null) is 0, and
    // 0 is a number this screen is entitled to send. An admin who backspaced the box to retype it
    // and then saved was freezing every kind's wording — invoice, credit note, statement, rental
    // contract, notes and payslip — under a success message.
    if (!readableLimit(form.changesPerYear)) {
      this.docsError.set(this.i18n.t('store.docs.limit.invalid'));
      return false;
    }
    const limit = Number(form.changesPerYear);
    this.docsBusy.set(true);
    this.docsError.set(null);
    this.docsSaved.set(false);
    try {
      const dto = await this.documentsApi.put({
        documentTexts,
        email: form.email.trim() || null,
        website: form.website.trim() || null,
        bankDetails: form.bankDetails.trim() || null,
        changesPerYear: limit,
        logoUrl: form.logoUrl,
        titleLanguages: form.titleLanguages,
        itemNameLanguages: form.itemName ? [form.itemName, ...(form.itemNameBeneath ? [form.itemNameBeneath] : [])] : [],
        contractWordings,
      });
      this.logoPending.set(false);
      this.fresh.set([]);
      // What a save cost is only known once the server has counted it, so the allowances and the
      // history come back from the save itself rather than from the figures the screen had.
      this.applyAllowances(dto);
      this.docs.update((d) => (d ? { ...d, changesPerYear: dto.changesPerYear, logoUrl: dto.logoUrl ?? null } : d));
      await this.loadHistory();
      this.docsSaved.set(true);
      setTimeout(() => this.docsSaved.set(false), 2500);
      return true;
    } catch (e) {
      // Framed in the reader's language, with the server's own sentence kept inside it: that
      // sentence names the kind, how many changes were used and when the allowance resets, and
      // dropping it would leave a polite message that explains nothing. Refusing a wording
      // change is the one refusal the yearly limit makes routine.
      this.docsError.set(this.i18n.t('store.docs.refused', { message: adminErrorMessage(e) }));
      // What is left of the allowance may have moved under this save; read it back rather than
      // leaving the counters the screen happens to be holding.
      await this.loadHistory();
      return false;
    } finally {
      this.docsBusy.set(false);
    }
  }

  /**
   * Reads the picked file into the form as a data URL; nothing is sent until Save. A type or a
   * size the server would refuse is said here first, and the form keeps the logo it had.
   */
  async pickLogo(event: Event): Promise<void> {
    const input = event.target as HTMLInputElement;
    const file = input.files?.[0];
    // Cleared at once, so picking the same file again after a refusal still fires a change.
    input.value = '';
    if (!file || !this.docs()) return;
    const problem = logoFileProblem(file.type, file.size);
    if (problem) {
      this.logoProblem.set(
        problem === 'type'
          ? this.i18n.t('logo.badType')
          : this.i18n.t('logo.tooBig', { size: Math.ceil(file.size / 1024), kb: this.logoMaxKb }),
      );
      return;
    }
    try {
      const url = await readAsDataUrl(file);
      this.docs.update((d) => (d ? { ...d, logoUrl: url } : d));
      this.logoProblem.set(null);
      this.logoPending.set(true);
    } catch {
      this.logoProblem.set(this.i18n.t('logo.unreadable'));
    }
  }

  removeLogo(): void {
    this.docs.update((d) => (d ? { ...d, logoUrl: null } : d));
    this.logoProblem.set(null);
    this.logoPending.set(true);
  }

  missingRequired(): boolean {
    const values = this.form()?.fiscalIds ?? {};
    return this.profile().fiscalIds.some((id) => id.required && !values[id.key]?.trim());
  }

  async save(): Promise<void> {
    const form = this.form();
    if (!form) return;
    if (this.missingRequired()) return;
    if (!(await this.confirm.ask(saveRequest(this.i18n, this.companyName())))) return;

    // Only identifiers the profile still defines are sent; the server keeps the rest.
    const fiscalIds = Object.fromEntries(
      this.profile().fiscalIds.map((id) => [id.key, form.fiscalIds[id.key]?.trim() ?? '']),
    );
    // Blank is a choice too: it clears the company's face so the country profile decides again.
    const payload = { ...form, fiscalIds, uiFont: this.uiFont().trim() || null };
    this.busy.set(true);
    this.error.set(null);
    this.saved.set(false);
    try {
      const store = await this.admin.updateStore(payload);
      // The row goes into the local replica as well as to the server, because that replica is
      // what the screens read: the company's interface face, footer and loyalty rules all come
      // from PosService.store(). Without this the operator who just chose Cairo kept Plex until
      // the next pull — the back office waiting on sync exactly as a till does, on the one
      // screen where the person is looking at the thing they changed.
      await this.pos.applyStore(store);
      this.saved.set(true);
      setTimeout(() => this.saved.set(false), 2500);
    } catch (e) {
      this.error.set(adminErrorMessage(e));
    } finally {
      this.busy.set(false);
    }
  }
}

/**
 * Why a picked file cannot be the logo, or null: one of the four types the server takes, and no
 * larger than its cap. The browser's own type is trusted for the message only — the server checks
 * the bytes themselves.
 */
export function logoFileProblem(type: string, size: number): 'type' | 'size' | null {
  if (!LOGO_TYPES.includes(type)) return 'type';
  if (size > LOGO_MAX_BYTES) return 'size';
  return null;
}

/**
 * Whether the "changes per year" box holds a number this screen may send.
 *
 * <p>Exported so a spec can pin the case that bit: Angular's number control writes `null` into
 * the model when the field is emptied, and every "is it a number?" guard written around
 * `Number(...)` reads that null as a deliberate zero.</p>
 */
export function readableLimit(value: unknown): boolean {
  if (value === null || value === undefined || value === '') return false;
  const limit = Number(value);
  return Number.isInteger(limit) && limit >= 0;
}
