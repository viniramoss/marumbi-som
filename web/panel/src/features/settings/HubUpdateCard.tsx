import { useRef, useState, type ReactNode } from 'react';
import { CircleArrowUp, RefreshCw, Upload, X } from 'lucide-react';
import { useQueryClient } from '@tanstack/react-query';
import { errorMessage } from '../../api/client';
import {
  keys,
  useCancelHubUpdate,
  useHubUpdate,
  useRequestHubUpdate,
  useSearchHubUpdate,
  useSetHubAutoUpdate,
} from '../../api/queries';
import type { HubUpdateLast, HubUpdatePending, HubUpdateStatus } from '../../api/types';
import { pickReleaseFiles, uploadHubPackage } from '../../api/upload';
import { ConfirmDialog } from '../../components/Modal';
import { useToast } from '../../components/Toasts';
import { Badge, Button, Card, CardHeader, ErrorNote, ProgressBar, Spinner } from '../../components/ui';
import { addOneHour, formatBytes, formatDateTime, formatTime } from '../../lib/format';

interface Sending {
  name: string;
  fraction: number;
}

/**
 * Atualização do Hub (D32 f, D35): a versão nova chega do GitHub junto com a do agente e é instalada pela tarefa do
 * Windows, no botão ou sozinha de madrugada; **Buscar atualizações** procura na hora e já instala. O Hub fica fora do ar
 * por cerca de 1 minuto; as lojas continuam tocando.
 */
export function HubUpdateCard() {
  const status = useHubUpdate();
  const request = useRequestHubUpdate();
  const search = useSearchHubUpdate();
  const cancel = useCancelHubUpdate();
  const setAuto = useSetHubAutoUpdate();
  const client = useQueryClient();
  const toast = useToast();
  const input = useRef<HTMLInputElement>(null);
  const [sending, setSending] = useState<Sending | null>(null);
  const [problem, setProblem] = useState<string | null>(null);
  const [confirming, setConfirming] = useState(false);

  const send = async (files: File[]) => {
    setProblem(null);
    const picked = pickReleaseFiles(files);
    if (typeof picked === 'string') {
      setProblem(picked);
      return;
    }
    setSending({ name: picked.packageFile.name, fraction: 0 });
    try {
      const next = await uploadHubPackage(picked.packageFile, picked.signatureFile, (fraction) =>
        setSending({ name: picked.packageFile.name, fraction }),
      );
      client.setQueryData(keys.hubUpdate, next);
      toast.success(`Versão ${next.available?.version ?? ''} do Hub recebida. Ela está pronta para instalar.`);
    } catch (error) {
      setProblem(errorMessage(error));
    } finally {
      setSending(null);
    }
  };

  const hub = status.data;
  return (
    <Card>
      <CardHeader
        title={
          <span className="inline-flex items-center gap-2">
            <CircleArrowUp className="size-4 text-stone-400" /> Atualização do Hub
          </span>
        }
        actions={
          hub?.supported && (
            <Button
              variant="ghost"
              icon={<Upload className="size-4" />}
              busy={sending !== null}
              disabled={hub.pending !== null}
              onClick={() => input.current?.click()}
              title="Escolha o .zip e o .sig.json gerados pelo sign-release -HubDir (para testes)"
            >
              Enviar pacote…
            </Button>
          )
        }
      />
      <input
        ref={input}
        type="file"
        multiple
        accept=".zip,.json"
        className="hidden"
        data-testid="hub-package-input"
        onChange={(e) => {
          const files = Array.from(e.target.files ?? []);
          e.target.value = '';
          if (files.length > 0) void send(files);
        }}
      />
      {status.isPending ? (
        <Spinner />
      ) : !hub ? (
        <div className="p-5">
          <ErrorNote>{errorMessage(status.error)}</ErrorNote>
        </div>
      ) : (
        <div className="space-y-4 px-5 py-4 text-sm">
          <dl className="grid grid-cols-[auto_1fr] gap-x-4 gap-y-1.5">
            <dt className="text-stone-500">Instalada</dt>
            <dd className="text-right font-medium text-stone-800">{hub.currentVersion}</dd>
            <dt className="text-stone-500">Disponível</dt>
            <dd className="text-right font-medium text-stone-800">
              {hub.available ? (
                <span className="inline-flex flex-wrap items-center justify-end gap-2">
                  <Badge tone="green" dot>
                    {hub.available.version}
                  </Badge>
                  <span className="text-xs font-normal text-stone-500">
                    {formatBytes(hub.available.sizeBytes)} · baixada em {formatDateTime(hub.available.downloadedAt)}
                  </span>
                </span>
              ) : (
                <span className="font-normal text-stone-500">Nenhuma versão mais nova</span>
              )}
            </dd>
          </dl>

          {!hub.supported ? (
            <Note>
              Este Hub não está na instalação padrão (<code className="text-xs">C:\MarumbiSom\Hub</code>), então não se
              atualiza sozinho. Atualize rodando o <code className="text-xs">install-hub.ps1</code> da versão nova.
            </Note>
          ) : (
            <>
              {hub.download && (
                <div className="space-y-1">
                  <p className="text-stone-600">
                    Baixando o Hub {hub.download.version}… {formatBytes(hub.download.doneBytes)} de{' '}
                    {formatBytes(hub.download.totalBytes)}
                  </p>
                  <ProgressBar
                    value={
                      hub.download.totalBytes > 0 ? (hub.download.doneBytes / hub.download.totalBytes) * 100 : undefined
                    }
                    label={`Baixando o Hub ${hub.download.version} do GitHub`}
                  />
                </div>
              )}
              {hub.searching && !hub.download && (
                <div className="space-y-1">
                  <p className="text-stone-600">Procurando versão nova no GitHub…</p>
                  <ProgressBar label="Procurando versão nova do Hub no GitHub" />
                </div>
              )}
              {!hub.download &&
                !hub.searching &&
                hub.message &&
                (hub.problem ? <ErrorNote>{hub.message}</ErrorNote> : <p className="text-stone-600">{hub.message}</p>)}
              {sending && (
                <div className="space-y-1">
                  <p className="text-stone-600">
                    Enviando {sending.name}… {Math.round(sending.fraction * 100)}%
                    {sending.fraction >= 1 && ' (o Hub está conferindo o pacote)'}
                  </p>
                  <ProgressBar
                    value={sending.fraction >= 1 ? undefined : sending.fraction * 100}
                    label="Enviando o pacote do Hub"
                  />
                </div>
              )}
              {problem && <ErrorNote>{problem}</ErrorNote>}
              {!hub.updaterInstalled && (
                <Note>
                  O atualizador ainda não está instalado neste PC. Rode o{' '}
                  <code className="text-xs">install-hub.ps1</code> uma vez, como administrador, com a versão nova;
                  depois disso o Hub se atualiza sozinho.
                </Note>
              )}

              {hub.pending ? (
                <PendingUpdate
                  hub={hub}
                  pending={hub.pending}
                  offline={status.isError}
                  canceling={cancel.isPending}
                  onCancel={() =>
                    cancel.mutate(undefined, {
                      onSuccess: () => toast.success('Atualização do Hub cancelada.'),
                      onError: (error) => toast.error(errorMessage(error)),
                    })
                  }
                />
              ) : (
                <>
                  {hub.last && <LastUpdate last={hub.last} />}
                  {hub.available ? (
                    <div className="flex justify-end">
                      <Button
                        variant="primary"
                        icon={<CircleArrowUp className="size-4" />}
                        disabled={!hub.updaterInstalled}
                        onClick={() => setConfirming(true)}
                      >
                        Atualizar o Hub agora
                      </Button>
                    </div>
                  ) : (
                    <div className="flex flex-wrap items-center justify-between gap-3">
                      <p className="max-w-md text-xs text-stone-500">
                        Procura no GitHub e, se tiver versão nova, já instala. O painel sai do ar por cerca de 1 minuto;
                        as lojas continuam tocando.
                      </p>
                      <Button
                        variant="primary"
                        icon={<RefreshCw className="size-4" />}
                        busy={hub.searching || search.isPending || hub.download !== null}
                        disabled={!hub.updaterInstalled}
                        onClick={() =>
                          search.mutate(undefined, { onError: (error) => toast.error(errorMessage(error)) })
                        }
                      >
                        Buscar atualizações
                      </Button>
                    </div>
                  )}
                </>
              )}

              <label className="flex cursor-pointer items-start gap-2 border-t border-stone-100 pt-4">
                <input
                  type="checkbox"
                  checked={hub.autoUpdate}
                  disabled={setAuto.isPending}
                  onChange={(e) =>
                    setAuto.mutate(e.target.checked, { onError: (error) => toast.error(errorMessage(error)) })
                  }
                  className="mt-0.5 size-4 accent-brand-700"
                />
                <span>
                  <span className="font-medium text-stone-800">Atualizar sozinho de madrugada</span>
                  <span className="block text-xs text-stone-500">
                    Com versão nova baixada, o Hub se atualiza entre {hub.autoUpdateTime} e{' '}
                    {addOneHour(hub.autoUpdateTime)}, uma vez por versão. As lojas continuam tocando enquanto ele
                    reinicia.
                  </span>
                </span>
              </label>
            </>
          )}
        </div>
      )}

      {confirming && hub?.available && (
        <ConfirmDialog
          title={`Atualizar o Hub para a versão ${hub.available.version}?`}
          confirmLabel="Atualizar agora"
          danger={false}
          busy={request.isPending}
          onCancel={() => setConfirming(false)}
          onConfirm={() =>
            request.mutate(undefined, {
              onSuccess: () => toast.success('Atualização pedida. O atualizador do Windows começa em até 1 minuto.'),
              onError: (error) => toast.error(errorMessage(error)),
              onSettled: () => setConfirming(false),
            })
          }
        >
          <p>
            O painel sai do ar por cerca de 1 minuto enquanto o Hub troca de versão. As lojas continuam tocando a
            programação que já receberam.
          </p>
          <p>
            Antes da troca, o banco é copiado. Se a versão nova não subir em 2 minutos, o Hub volta sozinho para a{' '}
            {hub.currentVersion}, com o banco como estava.
          </p>
        </ConfirmDialog>
      )}
    </Card>
  );
}

const stageText: Record<string, (pending: HubUpdatePending, hub: HubUpdateStatus) => string> = {
  stopping: (_, hub) => `Parando o Hub e guardando a versão ${hub.currentVersion}…`,
  installing: (pending) => `Instalando a versão ${pending.version}…`,
  confirming: (pending) => `A versão ${pending.version} está subindo…`,
  restoring: (pending, hub) => `A versão ${pending.version} não subiu; voltando para a ${hub.currentVersion}…`,
};

function PendingUpdate({
  hub,
  pending,
  offline,
  canceling,
  onCancel,
}: {
  hub: HubUpdateStatus;
  pending: HubUpdatePending;
  offline: boolean;
  canceling: boolean;
  onCancel: () => void;
}) {
  const waiting = pending.stage === 'waiting';
  const text = offline
    ? `O Hub está trocando para a versão ${pending.version}. O painel volta sozinho em seguida.`
    : waiting
      ? `Atualização para a versão ${pending.version} pedida ${pending.automatic ? 'de madrugada' : 'pelo painel'} às ${formatTime(pending.requestedAt)}. O atualizador do Windows começa em até 1 minuto.`
      : (stageText[pending.stage]?.(pending, hub) ?? `Atualizando para a versão ${pending.version}…`);

  return (
    <div className="space-y-2 rounded-lg bg-stone-50 px-3 py-2.5 ring-1 ring-stone-200">
      <p className="text-stone-700">{text}</p>
      <ProgressBar label={`Atualizando o Hub para a versão ${pending.version}`} />
      {!offline && !waiting && (
        <p className="text-xs text-stone-500">O painel fica fora do ar por cerca de 1 minuto.</p>
      )}
      {waiting && pending.updaterLate && (
        <Note>
          O atualizador ainda não pegou o pedido. Confira no Agendador de Tarefas do Windows se a tarefa{' '}
          <strong className="font-medium">MarumbiSom Hub - atualizador</strong> existe; o{' '}
          <code className="text-xs">install-hub.ps1</code> cria ela. Sem resposta em 30 minutos, o pedido é desfeito.
        </Note>
      )}
      {waiting && !offline && (
        <div className="flex justify-end">
          <Button variant="ghost" icon={<X className="size-4" />} busy={canceling} onClick={onCancel}>
            Cancelar
          </Button>
        </div>
      )}
    </div>
  );
}

function LastUpdate({ last }: { last: HubUpdateLast }) {
  const when = `${formatDateTime(last.at)}${last.automatic ? ', de madrugada' : ''}`;
  if (last.outcome === 'done') {
    return (
      <p className="flex flex-wrap items-center gap-2 text-stone-600">
        <Badge tone="green" dot>
          Atualizado
        </Badge>
        Da {last.previousVersion} para a {last.version} em {when}.
      </p>
    );
  }
  if (last.outcome === 'rolledBack') {
    return (
      <Note>
        Em {when}, a versão {last.version} não subiu e o Hub voltou sozinho para a {last.previousVersion}, com o banco
        como estava.{last.error && ` Motivo: ${last.error}`}
      </Note>
    );
  }
  return (
    <ErrorNote>
      Em {when}, a atualização para a versão {last.version} não foi feita: {last.error ?? 'erro desconhecido'}. O Hub
      continuou na {last.previousVersion}.
    </ErrorNote>
  );
}

function Note({ children }: { children: ReactNode }) {
  return <p className="rounded-lg bg-amber-50 px-3 py-2 text-amber-900 ring-1 ring-amber-200">{children}</p>;
}
