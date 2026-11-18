import { Component, computed, inject, input, output, signal } from '@angular/core';
import { FormsModule } from '@angular/forms';
import { I18nService, LANGS } from '../../core/i18n.service';
import { TRANSLATIONS } from '../../core/i18n/translations';
import { LanguagePack } from '../../core/models';
import { LanguagePackSummary, PlatformService } from '../../core/platform.service';
import { platformText } from './platform-text';
import { ConfirmService } from '../../ui/confirm.service';
import { saveRequest } from '../../ui/confirm-requests';
import { BACKOFFICE_TRANSLATIONS } from '../../core/i18n/translations-backoffice';
import { ListFooterComponent } from '../../ui/list-footer.component';
import { createServerPager } from '../../ui/server-pager';
import { LanguagePackPagesApi } from './language-pack-pages.api';

/**
 * Platform-wide translations. The operator downloads a template (every app text in English),
 * has it translated, and uploads the result; companies then enable the language in their
 * country settings.
 */
@Component({
  selector: 'app-language-packs',
  standalone: true,
  imports: [FormsModule, ListFooterComponent],
  template: `
    <section class="packs">
      <h2>{{ text().packsTitle }}</h2>
      <p class="muted">{{ text().packsHint }}</p>
      @if (pager.total() || directionFilter() !== 'all') {
        <!-- Runs on the server over every pack (owner, 2026-11-17: "paginate in backend"). -->
        <div class="ui-toolbar" role="search">
          <div class="ui-filter">
            <label for="lp-f-dir">{{ text().packDirection }}</label>
            <select id="lp-f-dir" [ngModel]="directionFilter()" (ngModelChange)="directionFilter.set($event)">
              <option ngValue="all">{{ i18n.t('filters.all') }}</option>
              <option ngValue="ltr">{{ text().ltr }}</option>
              <option ngValue="rtl">{{ text().rtl }}</option>
            </select>
          </div>
          @if (directionFilter() !== 'all') {
            <div class="ui-toolbar__end">
              <button type="button" class="btn-ghost" (click)="directionFilter.set('all')">{{ i18n.t('ui.state.clearFilters') }}</button>
            </div>
          }
        </div>
      }
      <div class="table-wrap">
        <table>
          <thead>
            <tr>
              <th>{{ text().packCode }}</th><th>{{ text().packName }}</th><th class="num">{{ text().packStrings }}</th>
              <th>{{ text().packDirection }}</th><th>{{ text().packActions }}</th>
            </tr>
          </thead>
          <tbody>
            @for (pack of pager.rows(); track pack.code) {
              <tr>
                <td class="mono" [attr.data-label]="text().packCode">{{ pack.code }}</td>
                <td [attr.dir]="pack.rtl ? 'rtl' : 'ltr'" [attr.data-label]="text().packName">{{ pack.label }}</td>
                <td class="num mono" [attr.data-label]="text().packStrings">{{ pack.strings }}</td>
                <td [attr.data-label]="text().packDirection">{{ pack.rtl ? text().rtl : text().ltr }}</td>
                <td class="actions" [attr.data-label]="text().packActions">
                  <button type="button" class="btn-ghost small" [disabled]="busy()" (click)="download(pack)">{{ text().downloadPack }}</button>
                  <button type="button" class="btn-danger small" [disabled]="busy()" (click)="remove(pack)">{{ text().deletePack }}</button>
                </td>
              </tr>
            } @empty {
              @if (directionFilter() !== 'all') {
                <tr>
                  <td colspan="5" class="empty">
                    {{ i18n.t('ui.state.noResults') }}
                    <button type="button" class="btn-ghost small" (click)="directionFilter.set('all')">{{ i18n.t('ui.state.clearFilters') }}</button>
                  </td>
                </tr>
              } @else {
                <tr><td colspan="5" class="empty">{{ text().noPacks }}</td></tr>
              }
            }
          </tbody>
        </table>
      </div>
      <app-list-footer [pager]="pager" [loading]="!pager.loaded()" />

      <div class="upload">
        <button type="button" class="btn-ghost" (click)="downloadTemplate()">{{ text().downloadTemplate }}</button>
        <label class="file">{{ text().uploadPack }}
          <input type="file" accept="application/json,.json" [disabled]="busy()" (change)="pick($event)" />
        </label>
      </div>
      @if (problem()) { <p class="notice danger" role="alert">{{ problem() }}</p> }
      @if (pending(); as pack) {
        <form class="confirm" (ngSubmit)="save(pack)">
          <p>{{ readyText(pack) }}</p>
          <label>{{ text().reason }}<input name="packReason" [(ngModel)]="reason" maxlength="500" required /></label>
          <button type="submit" class="btn-primary" [disabled]="busy() || !reason.trim()">{{ text().savePack }}</button>
        </form>
      }
    </section>
  `,
  styleUrls: ['../../ui/ui-patterns.scss'],
  styles: [
    `
      :host { --pt-ink-2: var(--muted); }
      .packs { display: grid; gap: 0.9rem; }
      h2 { font-size: 1.15rem; margin: 0; }
      p { margin: 0; line-height: 1.6; max-width: 75ch; }
      p.muted { color: var(--pt-ink-2); }
      table { width: 100%; border-collapse: collapse; }
      th, td { text-align: start; padding: 0.55rem 0.7rem; border-bottom: 1px solid var(--border); }
      thead th {
        background: var(--surface-2);
        color: var(--pt-ink-2);
        font-size: 0.74rem;
        font-weight: 600;
        letter-spacing: 0.04em;
        text-transform: uppercase;
        white-space: nowrap;
      }
      tbody tr:last-child td { border-bottom: 0; }
      .num { text-align: end; font-variant-numeric: tabular-nums; }
      .actions { display: flex; gap: 0.4rem; justify-content: flex-end; flex-wrap: wrap; }
      .small { padding: 0.3rem 0.6rem; font-size: 0.8rem; }
      .empty { text-align: center; padding: 1.75rem 1rem; color: var(--pt-ink-2); }
      .upload { display: flex; flex-wrap: wrap; gap: 0.75rem; align-items: end; }
      label { display: grid; gap: 0.35rem; font-size: 0.86rem; margin: 0; color: var(--text); }
      .confirm {
        display: grid; gap: 0.75rem; background: var(--surface-2);
        border-radius: var(--radius); padding: 0.9rem;
      }
      .confirm button { justify-self: start; }
      .table-wrap { overflow-x: auto; }

      /* On a phone the row becomes a stack of label/value pairs rather than a sideways
         scroll; the labels come from each cell's own column name. */
      @media (max-width: 45rem) {
        .table-wrap { overflow-x: visible; }
        thead { position: absolute; width: 1px; height: 1px; margin: -1px; overflow: hidden; clip-path: inset(50%); }
        table, tbody, tr, td { display: block; }
        tr { padding: 0.6rem 0; border-bottom: 1px solid var(--border); }
        tbody tr:last-child { border-bottom: 0; }
        td { border: 0; padding: 0.2rem 0; display: flex; flex-wrap: wrap; gap: 0.15rem 0.7rem; align-items: baseline; text-align: start; }
        td::before { content: attr(data-label); flex: 0 0 6rem; color: var(--pt-ink-2); font-size: 0.78rem; font-weight: 600; }
        td.num { justify-content: space-between; }
        td.num::before { flex: 0 0 auto; }
        td.actions { justify-content: flex-start; padding-block-start: 0.4rem; }
        td.actions::before { flex: 0 0 100%; }
        td.empty { display: block; }
        td.empty::before { content: none; }
      }
    `,
  ],
})
export class LanguagePacksComponent {
  private readonly api = inject(PlatformService);
  private readonly pages = inject(LanguagePackPagesApi);
  readonly i18n = inject(I18nService);
  private readonly confirm = inject(ConfirmService);
  /** The back-office vocabulary travels in this route's chunk, never in a till's download.
   *  A field initialiser, so the keys are in place before the first render. */
  private readonly words = this.i18n.extend(BACKOFFICE_TRANSLATIONS);


  /** Every pack, as the portal loaded them: what a saved pack's revision is read from. The table pages from the server. */
  readonly packs = input<LanguagePackSummary[]>([]);
  readonly busy = input(false);
  /** Emitted after a pack is saved or deleted, so the portal reloads its lists. */
  readonly changed = output<void>();
  readonly failed = output<unknown>();

  readonly text = computed(() => platformText(this.i18n.lang()));

  /** The list's filter (owner, 2026-11-17), applied on the server. */
  readonly directionFilter = signal<'all' | 'ltr' | 'rtl'>('all');
  /** The packs a page at a time from the server ("paginate in backend"); a refusal goes to the portal, as every other. */
  readonly pager = createServerPager(
    (page) =>
      this.pages.languagePacksPage({ skip: page.skip, take: page.take, ...page.filters }).catch((error: unknown) => {
        this.failed.emit(error);
        throw error;
      }),
    {
      key: 'platform.languagePacks',
      filters: () => ({ direction: this.directionFilter() }),
    },
  );

  readonly pending = signal<LanguagePack | null>(null);
  readonly problem = signal('');
  reason = '';

  readyText(pack: LanguagePack): string {
    return this.text()
      .packReady.replace('{name}', pack.label)
      .replace('{code}', pack.code)
      .replace('{count}', String(Object.keys(pack.strings).length));
  }

  downloadTemplate(): void {
    const template: LanguagePack = {
      code: 'xx',
      label: 'Language name',
      rtl: false,
      fallbackLanguage: 'en',
      // Both dictionaries: the template is every text in the app, and half of them now travel
      // with the lazy routes rather than with the till's bundle.
      strings: { ...TRANSLATIONS.en, ...BACKOFFICE_TRANSLATIONS.en },
    };
    saveJson('language-pack-template.json', template);
  }

  async download(summary: LanguagePackSummary): Promise<void> {
    try {
      saveJson(`language-pack-${summary.code}.json`, await this.api.languagePack(summary.code));
    } catch (error) {
      this.failed.emit(error);
    }
  }

  async pick(event: Event): Promise<void> {
    const inputEl = event.target as HTMLInputElement;
    const file = inputEl.files?.[0];
    inputEl.value = '';
    this.problem.set('');
    this.pending.set(null);
    if (!file) return;
    try {
      const data = JSON.parse(await file.text()) as Partial<LanguagePack>;
      const code = String(data.code ?? '').trim().toLowerCase();
      const strings = data.strings && typeof data.strings === 'object' ? data.strings : null;
      if (!/^[a-z]{2,3}(-[a-z0-9]{2,8})?$/.test(code) || LANGS.some((l) => l.code === code) || !data.label || !strings)
        throw new Error('invalid');
      const fallback = LANGS.find((l) => l.code === data.fallbackLanguage)?.code ?? 'en';
      this.pending.set({ code, label: String(data.label).trim(), rtl: !!data.rtl, fallbackLanguage: fallback, strings });
      this.reason = '';
    } catch {
      this.problem.set(this.text().badPack);
    }
  }

  async save(pack: LanguagePack): Promise<void> {
    if (this.busy() || !this.reason.trim()) return;
    if (!(await this.confirm.ask(saveRequest(this.i18n, pack.label)))) return;
    const revision = this.packs().find((p) => p.code === pack.code)?.revision ?? 0;
    try {
      await this.api.saveLanguagePack(pack, revision, this.reason.trim());
      this.pending.set(null);
      this.changed.emit();
      void this.pager.reload();
    } catch (error) {
      this.failed.emit(error);
    }
  }

  async remove(pack: LanguagePackSummary): Promise<void> {
    const text = this.text();
    const yes = await this.confirm.ask({
      title: text.deletePackTitle.replace('{name}', pack.label || pack.code),
      message: text.confirmDeletePack,
      confirmLabel: text.deletePack,
      danger: true,
    });
    if (!yes) return;
    try {
      await this.api.deleteLanguagePack(pack.code);
      this.changed.emit();
      void this.pager.reload();
    } catch (error) {
      this.failed.emit(error);
    }
  }
}

function saveJson(name: string, value: unknown): void {
  const url = URL.createObjectURL(new Blob([JSON.stringify(value, null, 2)], { type: 'application/json' }));
  const link = document.createElement('a');
  link.href = url;
  link.download = name;
  link.click();
  URL.revokeObjectURL(url);
}
