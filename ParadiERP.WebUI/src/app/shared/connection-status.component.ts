import { ChangeDetectionStrategy, Component, computed, inject, input } from '@angular/core';
import { ConnectivityService } from '../core/connectivity.service';
import { I18nService } from '../core/i18n.service';
import { SyncService } from '../core/sync.service';

/**
 * The connection and sync state, in words (CLAUDE.md rule 2): *Online*, *Offline*, *Syncing…*,
 * with the dot and tint beside the word. Pressing it syncs now — the button the top bar always had,
 * moved into each layout's header.
 *
 * `tone` follows the ground it sits on: the till's header is ink, the back office's is white.
 */
@Component({
  selector: 'app-connection-status',
  standalone: true,
  changeDetection: ChangeDetectionStrategy.OnPush,
  template: `
    <button
      type="button"
      class="conn"
      [class.on-ink]="tone() === 'ink'"
      [class.online]="connectivity.online()"
      [class.offline]="!connectivity.online()"
      [disabled]="sync.syncing()"
      (click)="syncNow()"
      [title]="i18n.t('sync.now')"
    >
      <span class="dot" aria-hidden="true"></span>
      {{ label() }}
    </button>
  `,
  styles: [
    `
      .conn {
        display: inline-flex;
        align-items: center;
        gap: var(--sp-2);
        white-space: nowrap;
        border: 1px solid transparent;
        border-radius: var(--radius-pill);
        font-size: var(--fs-meta);
        font-weight: 600;
        min-height: var(--control-h-sm);
        padding: 0 var(--sp-4);
      }
      .dot {
        width: 8px;
        height: 8px;
        border-radius: 50%;
        background: currentColor;
        flex: none;
      }

      /* On white: the state pairs, each hue on its own tint. */
      .conn.online {
        background: var(--ok-soft);
        color: var(--ok);
      }
      /* Amber, not red: selling offline is a supported mode, not a failure. */
      .conn.offline {
        background: var(--warn-soft);
        color: var(--warn);
      }

      /* On ink (the till's header): the soft tints are the readable pair there (11:1); the full
         hues are not. Online is the dot and the word alone, so it does not compete with the
         till's pages; offline keeps its amber ground, because that is the state to notice. */
      .conn.on-ink {
        background: transparent;
        transition: background var(--t-fast) ease;
      }
      .conn.on-ink.online:hover {
        background: var(--on-ink-line);
      }
      .conn.on-ink.online {
        color: var(--ok-soft);
      }
      /* In dark mode the soft tint is a dark ground, so "online" on ink takes the lifted hue. */
      :host-context(html[data-mode='dark']) .conn.on-ink.online {
        color: var(--ok);
      }
      .conn.on-ink.offline {
        background: var(--warn-soft);
        color: var(--warn);
      }
      .conn.on-ink:focus-visible {
        outline-color: var(--accent);
        outline-offset: 2px;
      }
    `,
  ],
})
export class ConnectionStatusComponent {
  readonly i18n = inject(I18nService);
  readonly connectivity = inject(ConnectivityService);
  readonly sync = inject(SyncService);
  readonly tone = input<'ink' | 'light'>('light');

  readonly label = computed(() => {
    if (this.sync.syncing()) return this.i18n.t('status.syncing');
    return this.connectivity.online() ? this.i18n.t('status.online') : this.i18n.t('status.offline');
  });

  async syncNow(): Promise<void> {
    await this.sync.sync('toolbar');
  }
}
