import { Injectable, OnDestroy, inject, signal } from '@angular/core';
import { I18nService, LANGS } from './i18n.service';
import { Lang } from './models';
import { ConfirmService } from '../ui/confirm.service';

@Injectable({ providedIn: 'root' })
export class LanguageSwitchService implements OnDestroy {
  private readonly i18n = inject(I18nService);
  private readonly confirm = inject(ConfirmService);

  readonly pending = signal<Lang | null>(null);
  readonly secondsLeft = signal(0);

  private timerId: ReturnType<typeof setInterval> | null = null;
  private asking = false;

  async request(lang: Lang): Promise<boolean> {
    if (lang === this.i18n.lang() || this.pending() !== null || this.asking) {
      return false;
    }

    const target = this.i18n.langs().find((l) => l.code === lang);
    const label = target?.label ?? LANGS.find((l) => l.code === lang)?.label ?? lang;

    this.asking = true;
    let confirmed = false;
    try {
      confirmed = await this.confirm.ask({
        title: this.i18n.t('lang.confirm.title'),
        message: this.i18n.t('lang.confirm.message', { language: label }),
        confirmLabel: this.i18n.t('lang.confirm.ok'),
        focusConfirm: true,
      });
    } finally {
      this.asking = false;
    }

    if (!confirmed) {
      return false;
    }

    return new Promise<boolean>((resolve) => {
      this.clearTimer();
      let remaining = 5;
      this.pending.set(lang);
      this.secondsLeft.set(remaining);

      this.timerId = setInterval(() => {
        remaining--;
        this.secondsLeft.set(remaining);
        if (remaining <= 0) {
          this.clearTimer();
          try {
            this.i18n.setLang(lang);
          } finally {
            this.pending.set(null);
            this.secondsLeft.set(0);
            resolve(true);
          }
        }
      }, 1000);
    });
  }

  ngOnDestroy(): void {
    this.clearTimer();
    this.pending.set(null);
    this.secondsLeft.set(0);
  }

  private clearTimer(): void {
    if (this.timerId !== null) {
      clearInterval(this.timerId);
      this.timerId = null;
    }
  }
}
