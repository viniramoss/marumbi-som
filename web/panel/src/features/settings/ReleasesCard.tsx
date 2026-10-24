import { useRef, useState } from 'react';
import { Download, PackageCheck, Trash2, Upload } from 'lucide-react';
import { useQueryClient } from '@tanstack/react-query';
import { errorMessage } from '../../api/client';
import { keys, sendCommand, useDeleteRelease, useReleases, useSetCurrentRelease, useStores } from '../../api/queries';
import type { ReleaseInfo } from '../../api/types';
import { pickReleaseFiles, uploadRelease } from '../../api/upload';
import { ConfirmDialog } from '../../components/Modal';
import { useToast } from '../../components/Toasts';
import { Badge, Button, Card, CardHeader, ErrorNote, ProgressBar, Spinner } from '../../components/ui';
import { storesToUpdate } from '../../lib/agentUpdate';
import { formatBytes, formatDateTime } from '../../lib/format';

interface Sending {
  name: string;
  fraction: number;
}

/**
 * Versões do agente para distribuição (seção 12.2, D27 h): enviar o pacote assinado com barra de andamento, escolher a
 * versão e atualizar as lojas conectadas. O Hub confere SHA-256 e conteúdo; a assinatura, só o agente.
 */
export function ReleasesCard() {
  const releases = useReleases();
  const stores = useStores();
  const setCurrent = useSetCurrentRelease();
  const remove = useDeleteRelease();
  const client = useQueryClient();
  const toast = useToast();
  const input = useRef<HTMLInputElement>(null);
  const [sending, setSending] = useState<Sending | null>(null);
  const [problem, setProblem] = useState<string | null>(null);
  const [removing, setRemoving] = useState<ReleaseInfo | null>(null);
  const [confirmUpdate, setConfirmUpdate] = useState(false);
  const [updating, setUpdating] = useState(false);
  const current = releases.data?.find((r) => r.isCurrent);
  const outdated = storesToUpdate(stores.data ?? [], current);

  const send = async (files: File[]) => {
    setProblem(null);
    const picked = pickReleaseFiles(files);
    if (typeof picked === 'string') {
      setProblem(picked);
      return;
    }
    setSending({ name: picked.packageFile.name, fraction: 0 });
    try {
      const release = await uploadRelease(picked.packageFile, picked.signatureFile, (fraction) =>
        setSending({ name: picked.packageFile.name, fraction }),
      );
      toast.success(`Versão ${release.version} recebida e marcada para distribuição.`);
      void client.invalidateQueries({ queryKey: keys.releases });
    } catch (error) {
      setProblem(errorMessage(error));
    } finally {
      setSending(null);
    }
  };

  const updateAll = async () => {
    setUpdating(true);
    let started = 0;
    const failures: string[] = [];
    for (const store of outdated) {
      try {
        const outcome = await sendCommand(store.id, { command: 'updateAgent' });
        if (outcome.result === 'ok') started += 1;
        else failures.push(`${store.name}: ${outcome.error ?? outcome.result}`);
      } catch (error) {
        failures.push(`${store.name}: ${errorMessage(error)}`);
      }
    }
    setUpdating(false);
    setConfirmUpdate(false);
    if (started > 0)
      toast.success(started === 1 ? '1 loja começou a atualizar.' : `${started} lojas começaram a atualizar.`);
    if (failures.length > 0) toast.error(`Não atualizaram: ${failures.join('; ')}`);
  };

  return (
    <Card>
      <CardHeader
        title={
          <span className="inline-flex items-center gap-2">
            <Download className="size-4 text-stone-400" /> Versão do agente para distribuição
          </span>
        }
        actions={
          <Button
            icon={<Upload className="size-4" />}
            busy={sending !== null}
            onClick={() => input.current?.click()}
            title="Escolha o .zip e o .sig.json gerados pelo sign-release"
          >
            Enviar versão…
          </Button>
        }
      />
      <input
        ref={input}
        type="file"
        multiple
        accept=".zip,.json"
        className="hidden"
        data-testid="release-input"
        onChange={(e) => {
          const files = Array.from(e.target.files ?? []);
          e.target.value = '';
          if (files.length > 0) void send(files);
        }}
      />
      <div className="space-y-4 px-5 py-4">
        {sending && (
          <div className="space-y-1">
            <p className="text-sm text-stone-600">
              Enviando {sending.name}… {Math.round(sending.fraction * 100)}%
              {sending.fraction >= 1 && ' (o Hub está conferindo o pacote)'}
            </p>
            <ProgressBar
              value={sending.fraction >= 1 ? undefined : sending.fraction * 100}
              label="Enviando a versão do agente"
            />
          </div>
        )}
        {problem && <ErrorNote>{problem}</ErrorNote>}
        {releases.isPending ? (
          <Spinner />
        ) : releases.isError ? (
          <ErrorNote>{errorMessage(releases.error)}</ErrorNote>
        ) : releases.data.length === 0 ? (
          <p className="text-sm text-stone-500">
            Nenhuma versão enviada. No seu PC, gere o pacote com <code className="text-xs">sign-release.ps1</code> a
            partir do artefato <code className="text-xs">MarumbiSom.Agent-win-x64</code> do CI e envie os dois arquivos
            (.zip e .sig.json) aqui.
          </p>
        ) : (
          <ul className="divide-y divide-stone-100 rounded-lg ring-1 ring-stone-200" aria-label="Versões do agente">
            {releases.data.map((release) => (
              <li key={release.version} className="flex flex-wrap items-center gap-3 px-3 py-2.5 text-sm">
                <div className="min-w-0 flex-1">
                  <p className="flex items-center gap-2 font-medium text-stone-900">
                    {release.version}
                    {release.isCurrent && (
                      <Badge tone="green" dot>
                        Para distribuição
                      </Badge>
                    )}
                  </p>
                  <p className="text-xs text-stone-500">
                    {formatBytes(release.sizeBytes)} · enviada em {formatDateTime(release.createdAt)} ·{' '}
                    {release.storeCount === 1 ? '1 loja nesta versão' : `${release.storeCount} lojas nesta versão`}
                  </p>
                </div>
                {!release.isCurrent && (
                  <div className="flex gap-2">
                    <Button
                      variant="ghost"
                      icon={<PackageCheck className="size-4" />}
                      busy={setCurrent.isPending && setCurrent.variables === release.version}
                      onClick={() =>
                        setCurrent.mutate(release.version, {
                          onSuccess: () => toast.success(`A versão ${release.version} passa a ser a de distribuição.`),
                          onError: (error) => toast.error(errorMessage(error)),
                        })
                      }
                    >
                      Usar esta
                    </Button>
                    <Button
                      variant="ghost"
                      icon={<Trash2 className="size-4" />}
                      aria-label={`Remover a versão ${release.version}`}
                      onClick={() => setRemoving(release)}
                    />
                  </div>
                )}
              </li>
            ))}
          </ul>
        )}
        {current && (
          <div className="flex flex-wrap items-center justify-between gap-3 rounded-lg bg-stone-50 px-3 py-2.5 text-sm ring-1 ring-stone-200">
            <p className="text-stone-600">
              {outdated.length === 0
                ? 'Nenhuma loja conectada precisa de atualização.'
                : outdated.length === 1
                  ? `1 loja conectada está numa versão anterior à ${current.version}.`
                  : `${outdated.length} lojas conectadas estão numa versão anterior à ${current.version}.`}
            </p>
            <Button
              variant="primary"
              icon={<Download className="size-4" />}
              disabled={outdated.length === 0}
              onClick={() => setConfirmUpdate(true)}
            >
              Atualizar lojas
            </Button>
          </div>
        )}
      </div>

      {removing && (
        <ConfirmDialog
          title={`Remover a versão ${removing.version}?`}
          confirmLabel="Remover"
          busy={remove.isPending}
          onCancel={() => setRemoving(null)}
          onConfirm={() =>
            remove.mutate(removing.version, {
              onSuccess: () => toast.success(`Versão ${removing.version} removida do Hub.`),
              onError: (error) => toast.error(errorMessage(error)),
              onSettled: () => setRemoving(null),
            })
          }
        >
          <p>O pacote sai do Hub. As lojas que já estão nesta versão continuam funcionando.</p>
        </ConfirmDialog>
      )}
      {confirmUpdate && current && (
        <ConfirmDialog
          title={`Atualizar para a versão ${current.version}?`}
          confirmLabel={outdated.length === 1 ? 'Atualizar 1 loja' : `Atualizar ${outdated.length} lojas`}
          danger={false}
          busy={updating}
          onCancel={() => setConfirmUpdate(false)}
          onConfirm={() => void updateAll()}
        >
          <p>{outdated.map((s) => `${s.name} (${s.agentVersion ?? 'versão desconhecida'})`).join(', ')}.</p>
          <p>
            Cada loja baixa o pacote ({formatBytes(current.sizeBytes)}) sem parar a música. Ao instalar, a música para
            por alguns segundos. Se a versão nova não subir, a loja volta sozinha para a anterior em cerca de 2 minutos.
          </p>
          <p>Dica: atualize uma loja piloto primeiro, pela tela da loja.</p>
        </ConfirmDialog>
      )}
    </Card>
  );
}
