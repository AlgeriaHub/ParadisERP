import { Component } from '@angular/core';
import { ComponentFixture, TestBed } from '@angular/core/testing';
import { FormsModule } from '@angular/forms';
import { provideRouter } from '@angular/router';
import { I18nService } from '../core/i18n.service';
import { SelectSearchComponent } from './select-search.component';

@Component({ standalone: true, imports: [FormsModule, SelectSearchComponent], template: `
  <app-select-search />
  <label for="test-customer">Customer</label>
  <select id="test-customer" [(ngModel)]="customer" [disabled]="disabled" required>
    <option [ngValue]="null">Walk-in customer</option>
    @for (item of customers; track item.id) {
      <option [ngValue]="item" [disabled]="item.disabled">{{ item.name }}</option>
    }
  </select>
  <select id="test-number" [(ngModel)]="number"><option [ngValue]="0">Zero</option><option [ngValue]="19">Nineteen</option></select>
  <select id="test-raw" [value]="raw" (change)="raw = $any($event.target).value; changes = changes + 1">
    <option value="">None</option><option value="customer-b">Beta</option>
  </select>
  <select id="test-multiple" multiple><option>A</option><option>B</option></select>
  <button id="outside">Outside</button>
  <dialog id="test-dialog"><label for="test-modal">Branch</label><select id="test-modal"><option>A</option><option>B</option></select></dialog>
` })
class Host {
  customers = [
    {id:'a', name:'أَحْمَد', disabled:false},
    {id:'b', name:'Élodie', disabled:false},
    {id:'c', name:'Disabled customer', disabled:true},
  ];
  customer: Host['customers'][number] | null = null;
  number = 0;
  raw = '';
  changes = 0;
  disabled = false;
}

describe('SelectSearchComponent', () => {
  let fixture: ComponentFixture<Host>;
  const panel = () => document.querySelector<HTMLElement>('#select-search-panel')!;
  const input = () => document.querySelector<HTMLInputElement>('#select-search-input')!;
  const native = (id='test-customer') => document.querySelector<HTMLSelectElement>('#'+id)!;
  const options = () => Array.from(document.querySelectorAll<HTMLElement>('#select-search-list [role=option]'));
  function open(id='test-customer') {
    native(id).dispatchEvent(new PointerEvent('pointerdown', {bubbles:true,cancelable:true,button:0}));
    fixture.detectChanges();
  }
  function search(term: string) {
    input().value=term; input().dispatchEvent(new Event('input', {bubbles:true})); fixture.detectChanges();
  }
  function key(value:string) {
    input().dispatchEvent(new KeyboardEvent('keydown', {key:value,bubbles:true,cancelable:true})); fixture.detectChanges();
  }
  beforeEach(async () => {
    await TestBed.configureTestingModule({imports:[Host], providers:[provideRouter([]),
      {provide:I18nService,useValue:{t:(key:string)=>key}}]}).compileComponents();
    fixture=TestBed.createComponent(Host); fixture.detectChanges(); await fixture.whenStable(); fixture.detectChanges();
  });
  afterEach(()=>fixture.destroy());

  it('searches Arabic without diacritics/hamza and keeps the original object-valued option', async()=>{
    open(); expect(document.activeElement).toBe(input());
    search('احمد'); expect(options().length).toBe(1);
    options()[0].click(); fixture.detectChanges(); await fixture.whenStable();
    expect(fixture.componentInstance.customer).toBe(fixture.componentInstance.customers[0]);
    expect(panel().matches(':popover-open')).toBeFalse(); expect(document.activeElement).toBe(native());
  });
  it('matches Latin accents and restores the null/walk-in option', async()=>{
    open(); search('elodie'); options()[0].click(); fixture.detectChanges(); await fixture.whenStable();
    expect(fixture.componentInstance.customer?.id).toBe('b');
    open(); search('walk-in'); options()[0].click(); fixture.detectChanges(); await fixture.whenStable();
    expect(fixture.componentInstance.customer).toBeNull();
  });
  it('preserves number types from ngValue', async()=>{
    open('test-number'); search('Nineteen'); options()[0].click(); fixture.detectChanges(); await fixture.whenStable();
    expect(fixture.componentInstance.number).toBe(19);
  });
  it('sends the original native change event once, and none when cancelling or selecting the same option', ()=>{
    open('test-raw'); search('Beta'); options()[0].click(); fixture.detectChanges();
    expect(fixture.componentInstance.raw).toBe('customer-b'); expect(fixture.componentInstance.changes).toBe(1);
    open('test-raw'); search('Beta'); options()[0].click(); fixture.detectChanges();
    expect(fixture.componentInstance.changes).toBe(1);
    open('test-raw'); search('None'); key('Escape');
    expect(fixture.componentInstance.raw).toBe('customer-b'); expect(fixture.componentInstance.changes).toBe(1);
  });
  it('never picks a disabled option by click or Enter',()=>{
    open(); search('Disabled'); expect(options()[0].getAttribute('aria-disabled')).toBe('true');
    options()[0].click(); key('Enter'); expect(fixture.componentInstance.customer).toBeNull();
    expect(panel().matches(':popover-open')).toBeTrue();
  });
  it('keeps empty results open and does not clear the current value',()=>{
    open(); search('missing customer'); key('Enter'); expect(options().length).toBe(0);
    expect(panel().querySelector('.search-empty')).not.toBeNull(); expect(fixture.componentInstance.customer).toBeNull();
  });
  it('supports arrow/Enter selection without changing the value merely on focus',async()=>{
    open(); key('ArrowDown'); expect(fixture.componentInstance.customer).toBeNull();
    key('Enter'); fixture.detectChanges(); await fixture.whenStable();
    expect(fixture.componentInstance.customer).toBe(fixture.componentInstance.customers[0]);
  });
  it('closes on outside pointer and Tab without applying the search result',()=>{
    open(); search('elodie'); key('Tab'); expect(panel().matches(':popover-open')).toBeFalse();
    expect(fixture.componentInstance.customer).toBeNull();
    open(); document.querySelector('#outside')!.dispatchEvent(new PointerEvent('pointerdown',{bubbles:true,button:0}));
    expect(panel().matches(':popover-open')).toBeFalse();
  });
  it('leaves disabled selects and multi-select listboxes native',async()=>{
    fixture.componentInstance.disabled=true; fixture.detectChanges(); await fixture.whenStable();
    expect(native().disabled).toBeTrue(); open(); expect(panel().matches(':popover-open')).toBeFalse();
    open('test-multiple'); expect(panel().matches(':popover-open')).toBeFalse();
  });
  it('re-reads dynamically added options without fetching or changing the selection',async()=>{
    open(); fixture.componentInstance.customers.push({id:'d',name:'New customer',disabled:false});
    fixture.detectChanges(); await fixture.whenStable(); await new Promise(resolve=>setTimeout(resolve,0));
    expect(native().options.length).toBe(5); search('New customer');
    expect(options().length).toBe(1); expect(fixture.componentInstance.customer).toBeNull();
  });
  it('keeps the popup inside a native modal and Escape closes only the popup',()=>{
    const dialog=document.querySelector<HTMLDialogElement>('#test-dialog')!; dialog.showModal();
    open('test-modal'); expect(dialog.contains(panel())).toBeTrue(); expect(document.activeElement).toBe(input());
    key('Escape'); expect(dialog.open).toBeTrue(); expect(panel().matches(':popover-open')).toBeFalse(); dialog.close();
  });
  it('restores existing ARIA attributes on close',()=>{
    native().setAttribute('aria-controls','original'); open(); expect(native().getAttribute('aria-expanded')).toBe('true');
    key('Escape'); expect(native().getAttribute('aria-controls')).toBe('original');
    expect(native().hasAttribute('aria-expanded')).toBeFalse();
  });
});
