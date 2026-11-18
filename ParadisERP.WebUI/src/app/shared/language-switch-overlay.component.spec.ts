import { ComponentFixture, TestBed } from '@angular/core/testing';
import { signal } from '@angular/core';
import { I18nService } from '../core/i18n.service';
import { LanguageSwitchService } from '../core/language-switch.service';
import { Lang } from '../core/models';
import { LanguageSwitchOverlayComponent } from './language-switch-overlay.component';

describe('LanguageSwitchOverlayComponent', () => {
  let fixture: ComponentFixture<LanguageSwitchOverlayComponent>;
  let component: LanguageSwitchOverlayComponent;
  let pendingSignal: ReturnType<typeof signal<Lang | null>>;
  let secondsLeftSignal: ReturnType<typeof signal<number>>;

  beforeEach(async () => {
    pendingSignal = signal<Lang | null>(null);
    secondsLeftSignal = signal<number>(0);

    const mockSwitcher = {
      pending: pendingSignal,
      secondsLeft: secondsLeftSignal,
    };

    const mockI18n = {
      lang: signal<Lang>('fr'),
      langs: signal([{ code: 'fr' as Lang, label: 'Français' }, { code: 'ar' as Lang, label: 'العربية' }]),
      t: (key: string, params?: { language?: string; n?: number }) =>
        `${key}:${params?.language ?? ''}:${params?.n ?? ''}`,
    };

    await TestBed.configureTestingModule({
      imports: [LanguageSwitchOverlayComponent],
      providers: [
        { provide: LanguageSwitchService, useValue: mockSwitcher },
        { provide: I18nService, useValue: mockI18n },
      ],
    }).compileComponents();

    fixture = TestBed.createComponent(LanguageSwitchOverlayComponent);
    component = fixture.componentInstance;
    fixture.detectChanges();
  });

  it('does not render overlay when pending is null', () => {
    const el = fixture.nativeElement as HTMLElement;
    expect(el.querySelector('.overlay')).toBeNull();
  });

  it('renders overlay, countdown and message when a switch is pending', () => {
    pendingSignal.set('ar');
    secondsLeftSignal.set(5);
    fixture.detectChanges();

    const el = fixture.nativeElement as HTMLElement;
    expect(el.querySelector('.overlay')).not.toBeNull();
    expect(el.querySelector('.countdown')?.textContent?.trim()).toBe('5');
    expect(el.querySelector('.status-text')?.textContent).toContain('العربية');
    expect(el.querySelector('.status-text')?.textContent).toContain('5');
  });

  it('updates countdown dynamically as seconds decrease', () => {
    pendingSignal.set('ar');
    secondsLeftSignal.set(3);
    fixture.detectChanges();

    const el = fixture.nativeElement as HTMLElement;
    expect(el.querySelector('.countdown')?.textContent?.trim()).toBe('3');

    secondsLeftSignal.set(1);
    fixture.detectChanges();
    expect(el.querySelector('.countdown')?.textContent?.trim()).toBe('1');
  });

  it('hides overlay when pending resets to null', () => {
    pendingSignal.set('ar');
    secondsLeftSignal.set(2);
    fixture.detectChanges();
    expect(fixture.nativeElement.querySelector('.overlay')).not.toBeNull();

    pendingSignal.set(null);
    secondsLeftSignal.set(0);
    fixture.detectChanges();
    expect(fixture.nativeElement.querySelector('.overlay')).toBeNull();
  });
});
