import { Component, booleanAttribute, input, output, signal } from '@angular/core';
import { ComponentFixture, TestBed } from '@angular/core/testing';
import { Router, provideRouter } from '@angular/router';
import { AuthService } from '../../../core/auth.service';
import { BranchFace } from '../../../core/branch-face';
import { I18nService } from '../../../core/i18n.service';
import { SyncService } from '../../../core/sync.service';
import { ConnectionStatusComponent } from '../../../shared/connection-status.component';
import { ConfirmService } from '../../../ui/confirm.service';
import { BackofficeShellComponent } from './backoffice-shell.component';
import { NavSearchComponent } from './nav-search.component';
import { SidebarComponent } from './sidebar.component';
import { WorkingBranchComponent } from './working-branch.component';

@Component({ selector: 'app-bo-sidebar', standalone: true, template: '' })
class SidebarStub {
  readonly collapsed = input(false);
  readonly drawer = input(false, { transform: booleanAttribute });
  readonly navigated = output<void>();
  readonly closeRequested = output<void>();
}

@Component({ selector: 'app-bo-nav-search', standalone: true, template: '' })
class NavSearchStub {}

@Component({ selector: 'app-working-branch', standalone: true, template: '' })
class WorkingBranchStub {}

@Component({ selector: 'app-connection-status', standalone: true, template: '' })
class ConnectionStatusStub {}

@Component({ standalone: true, template: '' })
class Blank {}

/**
 * The user's own password is changed from the back office's user menu (owner, 2026-10-05): one
 * entry, beside sign-out, that comes back to the page it was opened from.
 */
describe('BackofficeShellComponent — the user menu', () => {
  let fixture: ComponentFixture<BackofficeShellComponent>;

  beforeEach(async () => {
    TestBed.configureTestingModule({
      imports: [BackofficeShellComponent],
      providers: [
        provideRouter([
          { path: 'backoffice/admin/users', component: Blank },
          { path: 'change-password', component: Blank },
        ]),
        {
          provide: AuthService,
          useValue: {
            currentUser: signal({ id: 'u1', username: '58310427', fullName: 'Nadia Benali', role: 'Manager' }),
            logout: () => Promise.resolve(),
          },
        },
        { provide: BranchFace, useValue: { logo: signal(null), name: signal('') } },
        { provide: SyncService, useValue: { stats: signal({ pending: 0 }) } },
        { provide: ConfirmService, useValue: { ask: () => Promise.resolve(false) } },
      ],
    });
    TestBed.overrideComponent(BackofficeShellComponent, {
      remove: { imports: [SidebarComponent, WorkingBranchComponent, ConnectionStatusComponent, NavSearchComponent] },
      add: { imports: [SidebarStub, WorkingBranchStub, ConnectionStatusStub, NavSearchStub] },
    });
    await TestBed.inject(Router).navigateByUrl('/backoffice/admin/users');
    fixture = TestBed.createComponent(BackofficeShellComponent);
    fixture.detectChanges();
    await fixture.whenStable();
    fixture.detectChanges();
  });

  const i18n = () => TestBed.inject(I18nService);
  const items = () =>
    [...fixture.nativeElement.querySelectorAll('.gh__user .app-menu-item, .gh__user .app-menu-separator')] as HTMLElement[];
  const entry = () => fixture.nativeElement.querySelector('.gh__user [data-change-password]') as HTMLAnchorElement | null;

  it('offers "Change password" once, in the user menu, before the rule and sign-out', () => {
    const all = [...fixture.nativeElement.querySelectorAll('[data-change-password]')];
    expect(all.length).toBe(1);
    expect(entry()?.textContent?.trim()).toBe(i18n().t('account.password.title'));
    const order = items().map((el) => (el.hasAttribute('data-change-password') ? 'change' : el.getAttribute('role')));
    expect(order.indexOf('change')).toBeLessThan(order.indexOf('separator'));
    expect(items().at(-1)?.textContent?.trim()).toBe(i18n().t('nav.logout'));
  });

  it('opens the account screen with the page it was opened from', async () => {
    expect(entry()?.getAttribute('href')).toBe('/change-password?from=%2Fbackoffice%2Fadmin%2Fusers');
    entry()!.click();
    await fixture.whenStable();
    expect(TestBed.inject(Router).url).toBe('/change-password?from=%2Fbackoffice%2Fadmin%2Fusers');
  });
});
