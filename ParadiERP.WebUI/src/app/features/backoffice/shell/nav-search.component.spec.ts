import { Component, signal } from '@angular/core';
import { ComponentFixture, TestBed } from '@angular/core/testing';
import { Router, provideRouter } from '@angular/router';
import { AuthService } from '../../../core/auth.service';
import { I18nService } from '../../../core/i18n.service';
import { BACKOFFICE_TRANSLATIONS } from '../../../core/i18n/translations-backoffice';
import { MaintenanceModule } from '../../../core/maintenance-module.service';
import { PosUser } from '../../../core/models';
import { SubscriptionStatus } from '../../../core/subscription-status';
import { NavSearchComponent } from './nav-search.component';

@Component({ standalone: true, template: '' })
class Blank {}

describe('NavSearchComponent', () => {
  let fixture: ComponentFixture<NavSearchComponent>;
  let currentUser: ReturnType<typeof signal<PosUser | null>>;

  beforeEach(() => {
    currentUser = signal<PosUser | null>({
      id: 'u1',
      username: '10000001',
      fullName: 'Admin User',
      role: 'Admin',
      pinHash: '',
      isActive: true,
      version: 1,
    });

    TestBed.configureTestingModule({
      imports: [NavSearchComponent],
      providers: [
        provideRouter([
          { path: 'backoffice/inventory/products', component: Blank },
          { path: 'register', component: Blank },
        ]),
        { provide: AuthService, useValue: { currentUser } },
        { provide: SubscriptionStatus, useValue: { status: signal('Unrestricted') } },
        { provide: MaintenanceModule, useValue: { on: signal(false) } },
      ],
    });

    TestBed.inject(I18nService).extend(BACKOFFICE_TRANSLATIONS);
    fixture = TestBed.createComponent(NavSearchComponent);
    fixture.detectChanges();
  });

  const i18n = () => TestBed.inject(I18nService);
  const box = () => fixture.nativeElement.querySelector('input.search__input') as HTMLInputElement;
  const items = () => [...fixture.nativeElement.querySelectorAll('.search__item')] as HTMLAnchorElement[];

  const type = (value: string) => {
    const input = box();
    input.value = value;
    input.dispatchEvent(new Event('focus'));
    input.dispatchEvent(new Event('input'));
    fixture.detectChanges();
  };

  it('lists the products page when an Admin types part of its label', () => {
    const label = i18n().t('backoffice.tabs.products');
    type(label.slice(0, 4));

    const labels = items().map((el) => el.querySelector('.search__label')?.textContent?.trim());
    expect(labels).toContain(label);
  });

  it('hides pages the user may not open (a Cashier sees no products page)', () => {
    currentUser.set({
      id: 'u2',
      username: '10000002',
      fullName: 'Cashier User',
      role: 'Cashier',
      pinHash: '',
      isActive: true,
      version: 1,
    });
    fixture.detectChanges();

    const label = i18n().t('backoffice.tabs.products');
    type(label.slice(0, 4));

    const labels = items().map((el) => el.querySelector('.search__label')?.textContent?.trim());
    expect(labels).not.toContain(label);
    expect(items().length).toBe(0);
    expect(fixture.nativeElement.querySelector('.search__none')).not.toBeNull();
  });

  it('navigates to the active result on Enter and empties the box', async () => {
    const label = i18n().t('backoffice.tabs.products');
    type(label);

    expect(items().length).toBeGreaterThan(0);
    box().dispatchEvent(new KeyboardEvent('keydown', { key: 'Enter', bubbles: true, cancelable: true }));
    await fixture.whenStable();
    fixture.detectChanges();

    expect(TestBed.inject(Router).url).toBe('/backoffice/inventory/products');
    expect(box().value).toBe('');
    expect(fixture.componentInstance.query()).toBe('');
  });

  it('focuses the search input when «/» is pressed on the document, but not inside an <input>', () => {
    const other = document.createElement('input');
    document.body.appendChild(other);
    try {
      other.focus();
      other.dispatchEvent(new KeyboardEvent('keydown', { key: '/', bubbles: true, cancelable: true }));
      expect(document.activeElement).toBe(other);

      other.blur();
      document.dispatchEvent(new KeyboardEvent('keydown', { key: '/', bubbles: true, cancelable: true }));
      expect(document.activeElement).toBe(box());
    } finally {
      other.remove();
    }
  });
});
