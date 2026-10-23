import { useState, type FormEvent } from 'react';
import { Navigate, useLocation, useNavigate } from 'react-router';
import { useQueryClient } from '@tanstack/react-query';
import { api, statusMessage } from '../../api/client';
import { keys, useSession } from '../../api/queries';
import { Button, ErrorNote, Field, Input } from '../../components/ui';

export function LoginPage() {
  const session = useSession();
  const client = useQueryClient();
  const navigate = useNavigate();
  const location = useLocation();
  const [username, setUsername] = useState('admin');
  const [password, setPassword] = useState('');
  const [error, setError] = useState<string | null>(null);
  const [busy, setBusy] = useState(false);
  const from = (location.state as { from?: string } | null)?.from ?? '/';

  if (session.data) return <Navigate to={from} replace />;

  const submit = async (event: FormEvent) => {
    event.preventDefault();
    setBusy(true);
    setError(null);
    try {
      const { data, error: failure, response } = await api.POST('/api/auth/login', { body: { username, password } });
      if (!response.ok || !data) {
        setError(failure && 'error' in failure ? failure.error : statusMessage(response.status));
        return;
      }
      client.setQueryData(keys.session, data);
      void navigate(from, { replace: true });
    } catch {
      setError('Sem conexão com o Hub.');
    } finally {
      setBusy(false);
    }
  };

  return (
    <div className="flex min-h-dvh items-center justify-center bg-stone-900 px-4">
      <form onSubmit={(e) => void submit(e)} className="w-full max-w-sm space-y-5 rounded-2xl bg-white p-7 shadow-xl">
        <div className="flex items-center gap-3">
          <img src="/favicon.svg" alt="" className="size-10" />
          <div>
            <h1 className="text-lg font-semibold">Marumbi Som</h1>
            <p className="text-sm text-stone-500">Painel das lojas</p>
          </div>
        </div>
        <Field label="Usuário">
          <Input autoComplete="username" value={username} onChange={(e) => setUsername(e.target.value)} required />
        </Field>
        <Field label="Senha">
          <Input
            type="password"
            autoComplete="current-password"
            value={password}
            onChange={(e) => setPassword(e.target.value)}
            required
            autoFocus
          />
        </Field>
        {error && <ErrorNote>{error}</ErrorNote>}
        <Button type="submit" variant="primary" size="lg" busy={busy} className="w-full">
          Entrar
        </Button>
        <p className="text-center text-xs text-stone-500">A sessão fica salva neste aparelho por 30 dias.</p>
      </form>
    </div>
  );
}
