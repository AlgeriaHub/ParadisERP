import { Component, DestroyRef, NgZone, computed, inject, signal } from '@angular/core';
import { takeUntilDestroyed } from '@angular/core/rxjs-interop';
import { SwUpdate } from '@angular/service-worker';
import { I18nService } from '../core/i18n.service';
import { PosService } from '../core/pos.service';

/** How often the till asks the server whether a new bundle has been published. */
const CHECK_EVERY_MS = 60_000;

/**
 * Says, in words, that a new version of the app has finished downloading — and reloads only
 * when the cashier says so.
 *
 * <p>Until this existed, a deployment reached the tills whenever someone happened to close the
 * browser: every terminal ran yesterday's bundle against today's API, and the symptom was a
 * route that had quietly changed shape answering 400 to one till and not another. The service
 * worker already fetched the new files; nothing ever mentioned it.</p>
 *
 * <p>Two rules, both from the first principle in CLAUDE.md — never block the sale:</p>
 * <ul>
 *   <li>The reload is a click, never automatic. A reload decided by a timer would be a reload
 *       in the middle of someone's payment.</li>
 *   <li>With a cart on screen the action is refused and <em>says why</em>, because a reload
 *       throws away the lines the cashier has scanned but not yet taken money for. Sales
 *       already completed are in IndexedDB and survive anything.</li>
 * </ul>
 */
@Component({
  selector: 'app-update-banner',
  standalone: true,
  template: `
    @if (visible()) {
      <div class="update-banner" role="status">
        <span class="words">
          <strong>{{ i18n.t('update.ready') }}</strong>
          <small>{{ blocked() ? i18n.t('update.finishSaleFirst') : i18n.t('update.hint') }}</small>
        </span>
        <span class="actions">
          <button type="button" class="reload" [disabled]="blocked()" (click)="reload()">
            {{ i18n.t('update.reload') }}
          </button>
          <button type="button" class="later" (click)="later()">{{ i18n.t('update.later') }}</button>
        </span>
      </div>
    }
  `,
  styles: [
    `
      .update-banner {
        display: flex;
        flex-wrap: wrap;
        align-items: center;
        justify-content: space-between;
        gap: var(--sp-4);
        padding: var(--sp-3) var(--sp-5);
        background: var(--ink);
        border-block-end: var(--line) solid var(--border-strong);
        color: var(--on-ink);
        font-size: var(--fs-sm);
      }

      .words {
        display: flex;
        flex-direction: column;
        gap: var(--sp-1);
        text-align: start;
      }

      .words strong {
        font-weight: 600;
        color: var(--on-ink);
      }

      .words small {
        color: var(--on-ink-muted);
        font-size: var(--fs-meta);
      }

      .actions {
        display: flex;
        align-items: center;
        gap: var(--sp-3);
        margin-inline-start: auto;
      }

      .reload {
        min-height: var(--control-h-sm);
        padding-inline: var(--sp-4);
        border: var(--line) solid var(--border-strong);
        border-radius: var(--radius);
        background: var(--accent);
        color: var(--on-accent);
        font: inherit;
        font-weight: 600;
        cursor: pointer;
        transition: transform var(--t-fast) ease, box-shadow var(--t-fast) ease;

        &:hover:not(:disabled) {
          transform: translate(calc(-2px * var(--sx)), -2px);
          box-shadow: var(--shadow-sm);
        }

        &:active:not(:disabled) {
          transform: none;
          box-shadow: none;
        }

        &:disabled {
          cursor: not-allowed;
          opacity: 0.55;
        }
      }

      .later {
        background: transparent;
        border: none;
        color: var(--on-ink-muted);
        font: inherit;
        font-weight: 500;
        font-size: var(--fs-sm);
        cursor: pointer;
        padding-inline: var(--sp-2);
        text-decoration: underline;
        text-underline-offset: 3px;

        &:hover {
          color: var(--on-ink);
        }
      }
    `,
  ],
})
export class UpdateBannerComponent {
  readonly i18n = inject(I18nService);
  private readonly pos = inject(PosService);
  private readonly zone = inject(NgZone);
  private readonly destroyRef = inject(DestroyRef);
  // Optional: ng test bootstraps components without the service-worker providers, and a banner
  // that cannot be shown is not a reason for a spec to fail.
  private readonly updates = inject(SwUpdate, { optional: true });

  private readonly ready = signal(false);
  private readonly postponed = signal(false);

  /** A cart with lines in it: the reload would throw them away, so it is refused and says so. */
  readonly blocked = computed(() => this.pos.itemCount() > 0);
  readonly visible = computed(() => this.ready() && !this.postponed());

  constructor() {
    if (!this.updates?.isEnabled) return;

    this.updates.versionUpdates.pipe(takeUntilDestroyed()).subscribe((event) => {
      // VERSION_READY and nothing else: the new files are already on disk, so the reload is
      // instant and works even if the line drops between the click and the restart.
      if (event.type === 'VERSION_READY') this.ready.set(true);
    });

    // Outside the zone: a check a minute must not schedule a change-detection pass a minute.
    this.zone.runOutsideAngular(() => {
      const ask = () => void this.updates?.checkForUpdate().catch(() => undefined);
      ask();
      const timer = setInterval(ask, CHECK_EVERY_MS);
      this.destroyRef.onDestroy(() => clearInterval(timer));
    });
  }

  async reload(): Promise<void> {
    if (this.blocked()) return;
    // Activate first, so the reload lands on the new bundle rather than fetching the old one
    // again and showing the same banner.
    try {
      await this.updates?.activateUpdate();
    } catch {
      // An activation that fails leaves the old version running; the reload below is still the
      // right thing to do, and the banner will simply come back.
    }
    location.reload();
  }

  /** Hidden until the next time the service worker reports a version — not remembered. */
  later(): void {
    this.postponed.set(true);
  }
}
