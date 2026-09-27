import {
  HttpContextToken,
  HttpEvent,
  HttpErrorResponse,
  HttpHandlerFn,
  HttpInterceptorFn,
  HttpRequest,
} from '@angular/common/http';
import { inject } from '@angular/core';
import { catchError, map, Observable, switchMap, throwError } from 'rxjs';

import { normalizeApiError } from '../api/api-error.adapter';
import { HTTP_REQUEST_DISPATCH_SIGNAL } from '../api/http-request-dispatch.context';
import { AuthSessionFacade } from './auth-session.facade';
import { CsrfTokenService } from './csrf-token.service';

const RETRIED_CSRF_FAILURE = new HttpContextToken<boolean>(() => false);

const UNSAFE_METHODS = new Set(['POST', 'PUT', 'PATCH', 'DELETE']);

export const authSessionInterceptor: HttpInterceptorFn = (request, next) => {
  const authSession = inject(AuthSessionFacade);
  const csrfTokens = inject(CsrfTokenService);
  const requestInfo = getRequestInfo(request.url);

  if (!requestInfo.isFirstPartyApi) {
    return next(request);
  }

  const cookieRequest = request.clone({ withCredentials: true });

  if (!isUnsafeMethod(cookieRequest.method)) {
    return next(cookieRequest).pipe(
      catchError((error) => handleApiError(error, cookieRequest, next, authSession, csrfTokens)),
    );
  }

  return csrfTokens.ensureToken(authSession.csrfCacheKey()).pipe(
    map((csrfToken) =>
      cookieRequest.clone({
        setHeaders: {
          [csrfToken.headerName]: csrfToken.token,
        },
      }),
    ),
    catchError((error) => throwError(() => normalizeApiError(error))),
    switchMap((csrfRequest) =>
      dispatchAfterTransportSubscription(csrfRequest, next).pipe(
        catchError((error) => handleApiError(error, csrfRequest, next, authSession, csrfTokens)),
      ),
    ),
  );
};

function handleApiError(
  error: unknown,
  request: HttpRequest<unknown>,
  next: HttpHandlerFn,
  authSession: AuthSessionFacade,
  csrfTokens: CsrfTokenService,
): Observable<HttpEvent<unknown>> {
  const normalized = normalizeApiError(error);

  if (normalized.httpStatus === 401) {
    return authSession.refreshCurrentUser().pipe(
      switchMap((snapshot) => {
        if (snapshot?.isAuthenticated) {
          return throwError(() => normalized);
        }

        csrfTokens.clearToken();
        authSession.handleTerminal401();
        return throwError(() => normalized);
      }),
    );
  }

  if (
    isRetryableCsrfStatus(normalized.httpStatus) &&
    isLikelyCsrfFailure(error, normalized.message)
  ) {
    csrfTokens.clearToken();

    if (isUnsafeMethod(request.method) && !request.context.get(RETRIED_CSRF_FAILURE)) {
      return csrfTokens.ensureToken(authSession.csrfCacheKey()).pipe(
        map((csrfToken) =>
          request.clone({
            context: request.context.set(RETRIED_CSRF_FAILURE, true),
            setHeaders: {
              [csrfToken.headerName]: csrfToken.token,
            },
          }),
        ),
        switchMap((retryRequest) => dispatchAfterTransportSubscription(retryRequest, next)),
        catchError((retryError) => throwError(() => normalizeApiError(retryError))),
      );
    }
  }

  return throwError(() => normalized);
}

function dispatchAfterTransportSubscription(
  request: HttpRequest<unknown>,
  next: HttpHandlerFn,
): Observable<HttpEvent<unknown>> {
  const signal = request.context.get(HTTP_REQUEST_DISPATCH_SIGNAL);
  if (!signal) {
    return next(request);
  }

  return new Observable<HttpEvent<unknown>>((subscriber) => {
    signal.notify();
    return next(request).subscribe(subscriber);
  });
}

function isUnsafeMethod(method: string): boolean {
  return UNSAFE_METHODS.has(method.toUpperCase());
}

function getRequestInfo(url: string): { readonly isFirstPartyApi: boolean } {
  const locationOrigin = globalThis.location?.origin ?? 'http://localhost';
  const parsedUrl = new URL(url, locationOrigin);
  const isSameOrigin = parsedUrl.origin === locationOrigin;
  const isApiPath = parsedUrl.pathname === '/api' || parsedUrl.pathname.startsWith('/api/');

  return {
    isFirstPartyApi: isSameOrigin && isApiPath,
  };
}

function isLikelyCsrfFailure(error: unknown, message: string): boolean {
  if (message.toLowerCase().includes('csrf')) {
    return true;
  }

  if (error instanceof HttpErrorResponse && typeof error.error?.error === 'string') {
    return error.error.error.toLowerCase().includes('csrf');
  }

  return false;
}

function isRetryableCsrfStatus(status: number): boolean {
  return status === 400 || status === 403 || status === 419;
}
