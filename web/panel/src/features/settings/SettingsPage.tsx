import { useState, type ReactNode } from 'react';
import { HardDrive, KeyRound, Server } from 'lucide-react';
import { errorMessage } from '../../api/client';
import { useChangePassword, useSession, useSettings } from '../../api/queries';
import { useToast } from '../../components/Toasts';
import { Badge, Button, Card, CardHeader, ErrorNote, Field, Input, PageHeader, Spinner } from '../../components/ui';
import { useNow } from '../../hooks/useNow';
import { formatAgo, formatBytes, formatDateTime, formatMegabytes } from '../../lib/format';
import { HubUpdateCard } from './HubUpdateCard';
import { ReleasesCard } from './ReleasesCard';

/** Mesma regra do Hub (PanelAuth.MinPasswordLength). */
export const MinPasswordLength = 10;

/** Configurações (seção 7.2): senha, backup, Hub (e a atualização dele, D32) e distribuição do agente. O Telegram ficou para depois (D21). */
export function SettingsPage() {
  return (
    <>
      <PageHeader title="Configurações" />
      <div className="grid items-start gap-6 xl:grid-cols-2">
        <PasswordCard />
        <div className="space-y-6">
          <HubCard />
          <HubUpdateCard />
          <ReleasesCard />
        </div>
      </div>
    </>
  );
}

function PasswordCard() {
  const session = useSession();
  const change = useChangePassword();
  const toast = useToast();
  const [current, setCurrent] = useState('');
  const [next, setNext] = useState('');
  const [confirm, setConfirm] = useState('');

  const problem =
    next.length > 0 && next.length < MinPasswordLength
      ? `A senha nova precisa ter pelo menos ${MinPasswordLength} caracteres.`
      : confirm.length > 0 && confirm !== next
        ? 'A confirmação não confere com a senha nova.'
        : null;
  const ready = current.length > 0 && next.length >= MinPasswordLength && confirm === next;

  const submit = async () => {
    try {
      await change.mutateAsync({ currentPassword: current, newPassword: next });
      toast.success('Senha trocada.');
      setCurrent('');
      setNext('');
      setConfirm('');
    } catch {
      // O erro aparece no próprio cartão.
    }
  };

  return (
    <Card>
      <CardHeader
        title={
          <span className="inline-flex items-center gap-2">
            <KeyRound className="size-4 text-stone-400" /> Trocar senha
          </span>
        }
        subtitle={session.data ? `Usuário ${session.data.username}` : undefined}
      />
      <form
        className="space-y-4 px-5 py-4"
        onSubmit={(e) => {
          e.preventDefault();
          if (ready) void submit();
        }}
      >
        <Field label="Senha atual">
          <Input
            type="password"
            autoComplete="current-password"
            value={current}
            onChange={(e) => setCurrent(e.target.value)}
          />
        </Field>
        <Field label="Senha nova" hint={`Pelo menos ${MinPasswordLength} caracteres.`}>
          <Input type="password" autoComplete="new-password" value={next} onChange={(e) => setNext(e.target.value)} />
        </Field>
        <Field label="Repita a senha nova">
          <Input
            type="password"
            autoComplete="new-password"
            value={confirm}
            onChange={(e) => setConfirm(e.target.value)}
          />
        </Field>
        {problem && <ErrorNote>{problem}</ErrorNote>}
        {change.isError && <ErrorNote>{errorMessage(change.error)}</ErrorNote>}
        <div className="flex justify-end">
          <Button type="submit" variant="primary" busy={change.isPending} disabled={!ready}>
            Trocar senha
          </Button>
        </div>
      </form>
    </Card>
  );
}

function HubCard() {
  const settings = useSettings();
  const now = useNow(60_000);

  return (
    <Card>
      <CardHeader
        title={
          <span className="inline-flex items-center gap-2">
            <Server className="size-4 text-stone-400" /> Hub e backup
          </span>
        }
      />
      {settings.isPending ? (
        <Spinner />
      ) : settings.isError ? (
        <div className="p-5">
          <ErrorNote>{errorMessage(settings.error)}</ErrorNote>
        </div>
      ) : (
        <dl className="divide-y divide-stone-100 text-sm">
          <Row label="Versão do Hub">{settings.data.hubVersion}</Row>
          <Row label="Pasta de dados">
            <code className="text-xs">{settings.data.dataDirectory}</code>
          </Row>
          <Row label="Disco livre">
            <span className="inline-flex items-center gap-1.5">
              <HardDrive className="size-3.5 text-stone-400" aria-hidden /> {formatMegabytes(settings.data.diskFreeMb)}
            </span>
          </Row>
          <Row label="Último backup">
            {settings.data.backup.lastAt ? (
              <span className="inline-flex flex-wrap items-center justify-end gap-2">
                {formatDateTime(settings.data.backup.lastAt)}
                <BackupAge lastAt={settings.data.backup.lastAt} now={now} />
              </span>
            ) : (
              <Badge tone="amber">Nenhum backup ainda</Badge>
            )}
          </Row>
          {settings.data.backup.lastFile && (
            <Row label="Arquivo">
              <span className="text-xs">
                {settings.data.backup.lastFile} · {formatBytes(settings.data.backup.lastSizeBytes)}
              </span>
            </Row>
          )}
          <Row label="Backups guardados">
            {settings.data.backup.count} · {formatBytes(settings.data.backup.totalBytes)}
          </Row>
          <Row label="Pasta dos backups">
            <code className="text-xs">{settings.data.backup.directory}</code>
          </Row>
        </dl>
      )}
    </Card>
  );
}

/** O backup roda todo dia às 02:00 (seção 6.7); mais de 26 h sem backup merece atenção. */
function BackupAge({ lastAt, now }: { lastAt: string; now: number }) {
  const hours = (now - Date.parse(lastAt)) / 3_600_000;
  return <Badge tone={hours > 26 ? 'amber' : 'green'}>{formatAgo(lastAt, now)}</Badge>;
}

function Row({ label, children }: { label: string; children: ReactNode }) {
  return (
    <div className="flex items-center justify-between gap-4 px-5 py-2.5">
      <dt className="shrink-0 text-stone-500">{label}</dt>
      <dd className="min-w-0 text-right font-medium break-all text-stone-800">{children}</dd>
    </div>
  );
}
