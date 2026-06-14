import { Injectable, inject, signal } from '@angular/core';
import { HttpClient } from '@angular/common/http';
import { Observable, of, tap, switchMap, catchError } from 'rxjs';

export interface AuthResponse {
  success: boolean;
  accessToken?: string;
  refreshToken?: string;
  rememberMeToken?: string;
  username?: string;
  role?: string;
  error?: string;
}

export interface AuthState {
  isAuthenticated: boolean;
  isAnonymous: boolean;
  username?: string;
  role?: string;
  anonymousId?: string;
}

@Injectable({ providedIn: 'root' })
export class AuthService {
  private http = inject(HttpClient);
  private baseUrl = 'http://localhost:5000/api/auth';

  private _accessToken: string | null = null;
  private _refreshToken: string | null = null;

  authState = signal<AuthState>({ isAuthenticated: false, isAnonymous: false });
  blockMessage = signal<string | null>(null);

  private readonly REFRESH_KEY = 'rag_refresh_token';
  private readonly REMEMBER_COOKIE = 'rag_remember_me';
  private readonly ANON_ID_KEY = 'rag_anonymous_id';

  async initialize(): Promise<void> {
    const rememberToken = this.getRememberMeCookie();
    if (rememberToken) {
      try {
        await this.rememberMeLogin(rememberToken).toPromise();
        return;
      } catch {}
    }
    const refreshToken = localStorage.getItem(this.REFRESH_KEY);
    if (refreshToken) {
      try {
        await this.refresh(refreshToken).toPromise();
        return;
      } catch { localStorage.removeItem(this.REFRESH_KEY); }
    }
  }

  checkSetupRequired(): Observable<{ setupRequired: boolean }> {
    return this.http.get<{ setupRequired: boolean }>(`${this.baseUrl}/status`);
  }

  setup(username: string, password: string): Observable<AuthResponse> {
    return this.http.post<AuthResponse>(`${this.baseUrl}/setup`, { username, password }).pipe(
      switchMap(res => res.success ? this.login(username, password, false) : of(res))
    );
  }

  login(username: string, password: string, rememberMe: boolean): Observable<AuthResponse> {
    return this.http.post<AuthResponse>(`${this.baseUrl}/login`, { username, password, rememberMe }).pipe(
      tap(res => { if (res.success) this.applyAuthResponse(res, rememberMe); })
    );
  }

  refresh(refreshToken: string): Observable<AuthResponse> {
    return this.http.post<AuthResponse>(`${this.baseUrl}/refresh`, { refreshToken }).pipe(
      tap(res => { if (res.success) this.applyAuthResponse(res, false); })
    );
  }

  rememberMeLogin(token: string): Observable<AuthResponse> {
    return this.http.post<AuthResponse>(`${this.baseUrl}/remember`, { token }).pipe(
      tap(res => { if (res.success) this.applyAuthResponse(res, false); })
    );
  }

  logout(): void {
    if (this._refreshToken) {
      this.http.post(`${this.baseUrl}/logout`, { refreshToken: this._refreshToken })
        .pipe(catchError(() => of(null))).subscribe();
    }
    this.clearAuth();
  }

  logoutBlocked(message: string): void {
    this.blockMessage.set(message);
    this.clearAuth();
  }

  enterAnonymousMode(): void {
    this.authState.set({
      isAuthenticated: false,
      isAnonymous: true,
      anonymousId: this.getOrCreateAnonymousId()
    });
  }

  get accessToken(): string | null { return this._accessToken; }
  get isAdmin(): boolean { return this.authState().role === 'admin'; }

  updateAccessToken(token: string): void { this._accessToken = token; }

  getRefreshToken(): string | null {
    return this._refreshToken ?? localStorage.getItem(this.REFRESH_KEY);
  }

  private applyAuthResponse(res: AuthResponse, rememberMe: boolean): void {
    this._accessToken = res.accessToken!;
    this._refreshToken = res.refreshToken!;
    localStorage.setItem(this.REFRESH_KEY, res.refreshToken!);
    if (rememberMe && res.rememberMeToken) {
      this.setRememberMeCookie(res.rememberMeToken);
    }
    this.authState.set({
      isAuthenticated: true,
      isAnonymous: false,
      username: res.username,
      role: res.role
    });
  }

  private clearAuth(): void {
    this._accessToken = null;
    this._refreshToken = null;
    localStorage.removeItem(this.REFRESH_KEY);
    this.clearRememberMeCookie();
    this.authState.set({ isAuthenticated: false, isAnonymous: false });
  }

  private getOrCreateAnonymousId(): string {
    let id = localStorage.getItem(this.ANON_ID_KEY);
    if (!id) {
      const data = [navigator.userAgent, navigator.language,
        String(new Date().getTimezoneOffset()), String(screen.width), String(screen.height)].join('|');
      id = btoa(data).replace(/[^a-zA-Z0-9]/g, '').slice(0, 32);
      localStorage.setItem(this.ANON_ID_KEY, id);
    }
    return id;
  }

  private setRememberMeCookie(token: string): void {
    const exp = new Date();
    exp.setDate(exp.getDate() + 30);
    document.cookie = `${this.REMEMBER_COOKIE}=${encodeURIComponent(token)}; expires=${exp.toUTCString()}; path=/; SameSite=Strict`;
  }

  private getRememberMeCookie(): string | null {
    for (const c of document.cookie.split(';')) {
      const [k, v] = c.trim().split('=');
      if (k === this.REMEMBER_COOKIE && v) return decodeURIComponent(v);
    }
    return null;
  }

  private clearRememberMeCookie(): void {
    document.cookie = `${this.REMEMBER_COOKIE}=; expires=Thu, 01 Jan 1970 00:00:00 GMT; path=/`;
  }
}
