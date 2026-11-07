import { useState } from 'react';
import { KeyRound, RefreshCw } from 'lucide-react';
import { errorMessage } from '../../api/client';
import { useCheckGitHub, useGitHubStatus, useSaveGitHubToken } from '../../api/queries';
import { useToast } from '../../components/Toasts';
import { Badge, Button, ErrorNote, Field, Input, ProgressBar } from '../../components/ui';
import { formatBytes, formatDateTime } from '../../lib/format';

/**
 * Versões publicadas pelo GitHub Actions (D29): o Hub procura sozinho de tempos em tempos e baixa a mais nova. Aqui dá
 * para procurar na hora e acompanhar o download. O repositório é público (D33): o token só é preciso se ele voltar a
 * ser privado.
 */
export function GitHubReleases() {
  const status = useGitHubStatus();
  const check = useCheckGitHub();
  const saveToken = useSaveGitHubToken();
  const toast = useToast();
  const [editing, setEditing] = useState(false);
  const [token, setToken] = useState('');

  if (status.isPending) return null;
  if (status.isError) return <ErrorNote>{errorMessage(status.error)}</ErrorNote>;
  const github = status.data;
  if (!github.repository)
    return <p className="text-sm text-stone-500">A procura de versões no GitHub está desligada neste Hub.</p>;

  const download = github.download;
  const save = (value: string | null) =>
    saveToken.mutate(value, {
      onSuccess: () => {
        setEditing(false);
        setToken('');
        toast.success(value ? 'Token salvo. O Hub já está procurando a versão nova.' : 'Token removido do Hub.');
      },
      onError: (error) => toast.error(errorMessage(error)),
    });

  return (
    <section aria-label="Versões no GitHub" className="space-y-3 rounded-lg px-3 py-3 ring-1 ring-stone-200">
      <div className="flex flex-wrap items-start justify-between gap-3">
        <div className="min-w-0 text-sm">
          <p className="font-medium text-stone-900">Versões publicadas no GitHub</p>
          <p className="text-xs text-stone-500">
            {github.repository} · o Hub procura sozinho a cada {github.checkIntervalHours} h
            {github.checkedAt && ` · última procura ${formatDateTime(github.checkedAt)}`}
          </p>
        </div>
        <Button
          icon={<RefreshCw className="size-4" />}
          busy={github.checking || check.isPending}
          onClick={() => check.mutate(undefined, { onError: (error) => toast.error(errorMessage(error)) })}
        >
          Procurar agora
        </Button>
      </div>

      {download ? (
        <div className="space-y-1">
          <p className="text-sm text-stone-600">
            Baixando a versão {download.version}… {formatBytes(download.doneBytes)} de{' '}
            {formatBytes(download.totalBytes)}
          </p>
          <ProgressBar
            value={download.totalBytes > 0 ? (download.doneBytes / download.totalBytes) * 100 : undefined}
            label={`Baixando a versão ${download.version} do GitHub`}
          />
        </div>
      ) : github.checking ? (
        <p className="text-sm text-stone-600">Procurando versão nova…</p>
      ) : github.message ? (
        github.problem ? (
          <ErrorNote>{github.message}</ErrorNote>
        ) : (
          <p className="text-sm text-stone-600">{github.message}</p>
        )
      ) : null}

      {editing ? (
        <form
          className="flex flex-wrap items-end gap-2"
          onSubmit={(e) => {
            e.preventDefault();
            if (token.trim()) save(token.trim());
          }}
        >
          <div className="min-w-64 flex-1">
            <Field
              label="Token do GitHub"
              hint="Opcional: só é preciso se o repositório estiver privado. O Hub usa o token só para ler as versões e o guarda cifrado."
            >
              <Input
                type="password"
                autoComplete="off"
                spellCheck={false}
                placeholder="github_pat_…"
                value={token}
                onChange={(e) => setToken(e.target.value)}
              />
            </Field>
          </div>
          <div className="flex gap-2 pb-5">
            <Button type="submit" variant="primary" busy={saveToken.isPending} disabled={!token.trim()}>
              Salvar token
            </Button>
            <Button
              variant="ghost"
              onClick={() => {
                setEditing(false);
                setToken('');
              }}
            >
              Cancelar
            </Button>
          </div>
        </form>
      ) : !github.tokenConfigured ? (
        <div className="flex flex-wrap items-center gap-2 text-sm text-stone-500">
          <span>Sem token: o repositório é público.</span>
          <Button variant="ghost" icon={<KeyRound className="size-4" />} onClick={() => setEditing(true)}>
            Cadastrar token
          </Button>
        </div>
      ) : (
        <div className="flex flex-wrap items-center gap-2 text-sm">
          <Badge tone="green" dot>
            Token cadastrado
          </Badge>
          <Button variant="ghost" icon={<KeyRound className="size-4" />} onClick={() => setEditing(true)}>
            Trocar token
          </Button>
          <Button variant="ghost" busy={saveToken.isPending} onClick={() => save(null)}>
            Remover
          </Button>
        </div>
      )}
    </section>
  );
}
