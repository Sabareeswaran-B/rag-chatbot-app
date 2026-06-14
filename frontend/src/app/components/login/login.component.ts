import { Component, OnInit, inject, signal, output } from '@angular/core';
import { CommonModule } from '@angular/common';
import { FormsModule } from '@angular/forms';
import { AuthService } from '../../services/auth.service';

@Component({
  selector: 'app-login',
  standalone: true,
  imports: [CommonModule, FormsModule],
  templateUrl: './login.component.html',
  styleUrl: './login.component.scss'
})
export class LoginComponent implements OnInit {
  private authService = inject(AuthService);

  loginSuccess = output<void>();
  skipLogin = output<void>();

  username = signal('');
  password = signal('');
  rememberMe = signal(false);
  showPassword = signal(false);
  isLoading = signal(false);
  error = signal('');
  isSetupMode = signal(false);

  ngOnInit() {
    this.authService.checkSetupRequired().subscribe({
      next: res => this.isSetupMode.set(res.setupRequired),
      error: () => {}
    });
  }

  submit() {
    if (!this.username().trim() || !this.password().trim()) {
      this.error.set('Username and password are required.');
      return;
    }
    this.isLoading.set(true);
    this.error.set('');

    const action$ = this.isSetupMode()
      ? this.authService.setup(this.username(), this.password())
      : this.authService.login(this.username(), this.password(), this.rememberMe());

    action$.subscribe({
      next: res => {
        this.isLoading.set(false);
        if (res.success) this.loginSuccess.emit();
        else this.error.set(res.error || 'Login failed.');
      },
      error: err => {
        this.isLoading.set(false);
        this.error.set(err.error?.error || 'Connection failed. Is the backend running?');
      }
    });
  }

  onSkip() {
    this.authService.enterAnonymousMode();
    this.skipLogin.emit();
  }

  onKeyDown(e: KeyboardEvent) {
    if (e.key === 'Enter') this.submit();
  }
}
