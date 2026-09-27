import { ChangeDetectionStrategy, Component, inject } from '@angular/core';
import { RouterOutlet } from '@angular/router';

import { CoglatasThemeToggleComponent } from './shared/theme/coglatas-theme-toggle/coglatas-theme-toggle.component';
import { RealtimeFacade } from './core/realtime/realtime.facade';
import { I18nService } from './core/i18n/i18n.service';

@Component({
  changeDetection: ChangeDetectionStrategy.Eager,
  selector: 'app-root',
  imports: [RouterOutlet, CoglatasThemeToggleComponent],
  templateUrl: './app.html',
  styleUrl: './app.scss',
})
export class AppComponent {
  // Root ownership makes the transport lifecycle follow the authenticated app,
  // Rather than any individual product feature or route.
  private readonly realtime = inject(RealtimeFacade);

  constructor() {
    // Initialize the persisted display-language preference before any route renders.
    inject(I18nService);
  }
}
