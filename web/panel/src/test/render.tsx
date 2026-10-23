import type { ReactElement } from 'react';
import { QueryClient, QueryClientProvider } from '@tanstack/react-query';
import { render } from '@testing-library/react';
import { createMemoryRouter, RouterProvider } from 'react-router';
import { ToastProvider } from '../components/Toasts';

/** Renderiza uma tela com cache já preenchido (sem rede), avisos e um roteador em memória. */
export function renderScreen(ui: ReactElement, data: [readonly unknown[], unknown][] = [], path = '/') {
  const client = new QueryClient({ defaultOptions: { queries: { retry: false, staleTime: Infinity } } });
  for (const [key, value] of data) client.setQueryData(key, value);
  const router = createMemoryRouter([{ path: '*', element: ui }], { initialEntries: [path] });
  const result = render(
    <QueryClientProvider client={client}>
      <ToastProvider>
        <RouterProvider router={router} />
      </ToastProvider>
    </QueryClientProvider>,
  );
  return { ...result, client, router };
}
