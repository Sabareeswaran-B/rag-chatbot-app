import { Component, OnInit, inject, signal } from '@angular/core';
import { CommonModule } from '@angular/common';
import { ChatComponent } from './components/chat/chat.component';
import { FileUploadComponent } from './components/file-upload/file-upload.component';
import { LoginComponent } from './components/login/login.component';
import { AnalyticsComponent } from './components/analytics/analytics.component';
import { AuthService } from './services/auth.service';

type Tab = 'chat' | 'upload' | 'analytics';

@Component({
  selector: 'app-root',
  standalone: true,
  imports: [CommonModule, ChatComponent, FileUploadComponent, LoginComponent, AnalyticsComponent],
  templateUrl: './app.html',
  styleUrl: './app.scss'
})
export class App implements OnInit {
  authService = inject(AuthService);

  activeTab = signal<Tab>('chat');
  showLogin = signal(true);
  isInitialized = signal(false);

  async ngOnInit() {
    await this.authService.initialize();
    const state = this.authService.authState();
    if (state.isAuthenticated || state.isAnonymous) {
      this.showLogin.set(false);
    }
    this.isInitialized.set(true);
  }

  onLoginSuccess() { this.showLogin.set(false); }
  onSkipLogin() { this.showLogin.set(false); }

  onLogout() {
    this.authService.logout();
    this.activeTab.set('chat');
    this.showLogin.set(true);
  }

  setTab(tab: Tab) {
    if ((tab === 'upload' || tab === 'analytics') && !this.authService.isAdmin) return;
    this.activeTab.set(tab);
  }
}
