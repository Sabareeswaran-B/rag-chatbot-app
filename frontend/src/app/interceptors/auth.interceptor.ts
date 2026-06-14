import { HttpInterceptorFn, HttpRequest, HttpHandlerFn, HttpErrorResponse } from '@angular/common/http';
import { inject } from '@angular/core';
import { catchError, switchMap, throwError } from 'rxjs';
import { AuthService } from '../services/auth.service';

export const authInterceptor: HttpInterceptorFn = (req: HttpRequest<unknown>, next: HttpHandlerFn) => {
  const auth = inject(AuthService);

  if (req.url.includes('/api/auth/')) return next(req);

  let headers = req.headers;
  if (auth.accessToken) headers = headers.set('Authorization', `Bearer ${auth.accessToken}`);
  const state = auth.authState();
  if (state.isAnonymous && state.anonymousId) headers = headers.set('X-Anonymous-Id', state.anonymousId);

  const authReq = req.clone({ headers });

  return next(authReq).pipe(
    catchError((err: HttpErrorResponse) => {
      if (err.status === 401) {
        const refreshToken = auth.getRefreshToken();
        if (refreshToken) {
          return auth.refresh(refreshToken).pipe(
            switchMap(res => {
              if (res.success && res.accessToken) {
                auth.updateAccessToken(res.accessToken);
                return next(req.clone({
                  headers: req.headers.set('Authorization', `Bearer ${res.accessToken}`)
                }));
              }
              return throwError(() => err);
            }),
            catchError(() => throwError(() => err))
          );
        }
      }
      return throwError(() => err);
    })
  );
};
