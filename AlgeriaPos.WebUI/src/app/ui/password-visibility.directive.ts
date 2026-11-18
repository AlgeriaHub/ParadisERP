import { AfterViewInit, Directive, ElementRef, Renderer2, inject } from '@angular/core';
import { I18nService } from '../core/i18n.service';

/** Adds an accessible eye control to every password input that imports this directive. */
@Directive({ selector: 'input[type=password], input[appPasswordVisibility]', standalone: true })
export class PasswordVisibilityDirective implements AfterViewInit {
  private readonly input = inject(ElementRef<HTMLInputElement>);
  private readonly renderer = inject(Renderer2);
  private readonly i18n = inject(I18nService);

  ngAfterViewInit(): void {
    const button = this.renderer.createElement('button') as HTMLButtonElement;
    this.renderer.setAttribute(button, 'type', 'button');
    this.renderer.setAttribute(button, 'aria-pressed', 'false');
    this.renderer.setAttribute(button, 'aria-label', this.i18n.t('common.showPassword'));
    this.renderer.setAttribute(button, 'title', this.i18n.t('common.showPassword'));
    this.renderer.setAttribute(button, 'data-password-visibility', '');
    this.renderer.setStyle(button, 'margin-inline-start', '0.4rem');
    this.renderer.setStyle(button, 'min-inline-size', '2.5rem');
    this.renderer.setProperty(button, 'innerHTML', '<span aria-hidden="true">&#128065;</span>');
    this.renderer.listen(button, 'click', () => {
      const visible = this.input.nativeElement.type === 'password';
      this.renderer.setAttribute(this.input.nativeElement, 'type', visible ? 'text' : 'password');
      this.renderer.setAttribute(button, 'aria-pressed', String(visible));
      const label = this.i18n.t(visible ? 'common.hidePassword' : 'common.showPassword');
      this.renderer.setAttribute(button, 'aria-label', label);
      this.renderer.setAttribute(button, 'title', label);
      this.renderer.setProperty(button, 'innerHTML', `<span aria-hidden="true">${visible ? '&#128064;' : '&#128065;'}</span>`);
    });
    this.renderer.appendChild(this.input.nativeElement.parentNode, button);
  }
}
