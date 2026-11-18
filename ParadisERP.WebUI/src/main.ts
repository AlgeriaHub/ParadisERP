import { bootstrapApplication } from '@angular/platform-browser';
import { appConfig } from './app/app.config';
import { AppComponent } from './app/app.component';
import { initMonitoring } from './app/core/monitoring';

initMonitoring();

bootstrapApplication(AppComponent, appConfig)
  .catch((err) => console.error(err));
