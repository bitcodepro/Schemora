import { t, tError, tMessage, getLocale } from './i18n.ts';
function getSessionToken() {
  const fragment = new URLSearchParams(window.location.hash.slice(1));
  const received = fragment.get('token');
  if (received) {
    sessionStorage.setItem('schemora.session', received);
    history.replaceState(null, '', window.location.pathname + window.location.search);
  }
  return received || sessionStorage.getItem('schemora.session') || '';
}

export const sessionToken = getSessionToken();

export class ApiError extends Error {
  constructor(
    message: string,
    public readonly code: string,
    public readonly status: number,
    public readonly operationId?: string,
    public readonly detail?: string,
  ) {
    super(message);
    this.name = 'ApiError';
  }
}

export async function request<T>(url: string, options?: RequestInit): Promise<T> {
  const response = await fetch(url, {
    ...options,
    headers: { 'Content-Type': 'application/json', 'X-Schemora-Token': sessionToken, 'Accept-Language': getLocale(), ...options?.headers },
  });
  if (!response.ok) {
    const error = (await response.json().catch(() => null)) as {
      code?: string;
      message?: string;
      detail?: string;
      operationId?: string;
    } | null;
    if (response.status === 401 || (response.status === 403 && ['HOST_REJECTED', 'ORIGIN_REJECTED'].includes(error?.code || '')))
      throw new ApiError(
        t('Сеанс недействителен. Откройте Schemora по ссылке, созданной при запуске приложения.'),
        error?.code || 'SESSION_REQUIRED',
        response.status,
        error?.operationId,
      );
    throw new ApiError(
      [
        error?.message ? tError(error.code, error.message) : t('Ошибка запроса ({0})', response.status),
        error?.detail && tMessage(error.detail),
      ]
        .filter(Boolean)
        .join('\n'),
      error?.code || 'REQUEST_FAILED',
      response.status,
      error?.operationId || response.headers.get('X-Schemora-Operation') || undefined,
      error?.detail,
    );
  }
  const body = await response.text();
  return body.trim() ? (JSON.parse(body) as T) : (undefined as T);
}

export function errorMessage(cause: unknown, fallback: string): string {
  const message = tMessage(cause instanceof Error ? cause.message : fallback);
  return cause instanceof ApiError
    ? message + '\n' + cause.code + (cause.operationId ? ' · ' + t('Operation') + ': ' + cause.operationId : '')
    : message;
}
