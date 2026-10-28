import createClient, { type Middleware } from 'openapi-fetch';
import type { paths } from '../types/api';

/** Erro de uma chamada à API, com a mensagem em português que o Hub devolve. */
export class ApiFailure extends Error {
  readonly status: number;

  constructor(status: number, message: string) {
    super(message);
    this.name = 'ApiFailure';
    this.status = status;
  }
}

let onUnauthorized: () => void = () => {};

/** Chamado quando a sessão vence (qualquer 401 fora do login). */
export function setUnauthorizedHandler(handler: () => void) {
  onUnauthorized = handler;
}

const session: Middleware = {
  onResponse({ request, response }) {
    if (response.status === 401 && !new URL(request.url).pathname.startsWith('/api/auth/')) {
      onUnauthorized();
    }
    return response;
  },
};

export const api = createClient<paths>({
  baseUrl: typeof window === 'undefined' ? 'http://localhost' : window.location.origin,
  // Toda escrita precisa deste cabeçalho (proteção CSRF do Hub, D20 e).
  headers: { 'X-MarumbiSom': '1' },
  credentials: 'same-origin',
});
api.use(session);

/** Mensagem legível para um status sem corpo de erro. */
export function statusMessage(status: number): string {
  switch (status) {
    case 401:
      return 'Sessão encerrada. Entre de novo.';
    case 403:
      return 'Acesso negado.';
    case 404:
      return 'Não encontrado.';
    case 429:
      return 'Muitas tentativas. Espere 1 minuto.';
    default:
      return status >= 500 ? 'O Hub teve um erro. Veja o log do Hub.' : `Erro ${status}.`;
  }
}

function errorText(error: unknown, status: number): string {
  if (error && typeof error === 'object' && 'error' in error && typeof error.error === 'string') {
    return error.error;
  }
  return statusMessage(status);
}

/** Desembrulha a resposta do openapi-fetch: devolve os dados ou lança {@link ApiFailure}. */
export async function call<T>(request: Promise<{ data?: T; error?: unknown; response: Response }>): Promise<T> {
  let result: { data?: T; error?: unknown; response: Response };
  try {
    result = await request;
  } catch {
    throw new ApiFailure(0, 'Sem conexão com o Hub.');
  }
  const { data, error, response } = result;
  if (!response.ok) {
    throw new ApiFailure(response.status, errorText(error, response.status));
  }
  return data as T;
}

export function errorMessage(error: unknown): string {
  if (error instanceof ApiFailure) return error.message;
  if (error instanceof Error) return error.message;
  return 'Erro inesperado.';
}
