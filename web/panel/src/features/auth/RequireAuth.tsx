import { useEffect, type ReactNode } from 'react';
import { Navigate, useLocation, useNavigate } from 'react-router';
import { useQueryClient } from '@tanstack/react-query';
import { setUnauthorizedHandler } from '../../api/client';
import { keys, useSession } from '../../api/queries';
import { Spinner } from '../../components/ui';
import { LiveProvider } from '../../hooks/useLive';

/** Exige sessão; se ela vencer no meio do uso (401), volta para o login. */
export function RequireAuth({ children }: { children: ReactNode }) {
  const session = useSession();
  const location = useLocation();
  const navigate = useNavigate();
  const client = useQueryClient();

  useEffect(() => {
    setUnauthorizedHandler(() => {
      client.setQueryData(keys.session, null);
      void navigate('/entrar', { replace: true, state: { from: window.location.pathname } });
    });
  }, [client, navigate]);

  if (session.isPending) return <Spinner label="Abrindo o painel…" />;
  if (!session.data) return <Navigate to="/entrar" replace state={{ from: location.pathname }} />;
  return <LiveProvider>{children}</LiveProvider>;
}
