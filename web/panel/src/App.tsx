import { QueryClient, QueryClientProvider } from '@tanstack/react-query';
import { RouterProvider } from 'react-router';
import { ToastProvider } from './components/Toasts';
import { ApiFailure } from './api/client';
import { router } from './router';

const client = new QueryClient({
  defaultOptions: {
    queries: {
      // O WebSocket do painel avisa o que mudou; o resto é recarregado ao voltar para a aba.
      staleTime: 30_000,
      refetchOnWindowFocus: true,
      retry: (count, error) => !(error instanceof ApiFailure && error.status >= 400 && error.status < 500) && count < 2,
    },
  },
});

export function App() {
  return (
    <QueryClientProvider client={client}>
      <ToastProvider>
        <RouterProvider router={router} />
      </ToastProvider>
    </QueryClientProvider>
  );
}
