import { Injectable, effect, inject, DOCUMENT } from '@angular/core';

import { PosService } from './pos.service';
import { RegionalService } from './regional.service';
import { uiFontStack } from './ui-fonts';

/**
 * Applies the company's chosen interface face. Its only job is to keep the `--font-ui`
 * custom property in step with what the company chose, so the stylesheet decides what reads
 * it and no component needs to know a font was ever configurable.
 *
 * Two places can say what the face is, and they are read in that order: the company's own
 * `Store.uiFont`, set on the store screen and replicated to every till with the store row, and
 * failing that the country profile, which is the platform's default for companies that never
 * chose. So a till shows the company's face the moment the store row lands in its own database
 * — no extra request, and it works with the line down.
 *
 * Setting the property to an empty string removes it, so `var(--font-ui, <stack>)` in
 * styles.scss falls back to the built-in stack for the system face.
 */
@Injectable({ providedIn: 'root' })
export class UiFontService {
  private readonly regional = inject(RegionalService);
  private readonly pos = inject(PosService);
  private readonly document = inject(DOCUMENT);

  constructor() {
    effect(() => {
      // The profile is only read when the company has not chosen, which is also what keeps a
      // later profile change from repainting a company that has.
      const chosen = this.pos.store()?.uiFont?.trim() || this.regional.settings().uiFont;
      const stack = uiFontStack(chosen);
      const root = this.document.documentElement;
      if (stack) root.style.setProperty('--font-ui', stack);
      else root.style.removeProperty('--font-ui');
    });
  }
}
