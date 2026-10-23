import { useState, type ReactNode } from 'react';
import { useNavigate } from 'react-router';
import { Download, KeyRound, Power, Save, Trash2, Unlink } from 'lucide-react';
import { errorMessage } from '../../api/client';
import { useDeleteStore, useRevokeToken, useUpdateStore } from '../../api/queries';
import type { StoreDetail } from '../../api/types';
import { ConfirmDialog } from '../../components/Modal';
import { useToast } from '../../components/Toasts';
import { Button, Card, CardHeader, Field, Input, Select, SoonTag } from '../../components/ui';
import { useCommand } from '../../hooks/useCommand';
import { useUnsavedChanges } from '../../hooks/useUnsavedChanges';
import { EnrollCodeDialog } from './EnrollCode';
import { useGenerateEnrollCode } from './useGenerateEnrollCode';

const timezones = [
  ['America/Sao_Paulo', 'Brasília (São Paulo, Paraná, Sul e Sudeste)'],
  ['America/Bahia', 'Bahia'],
  ['America/Recife', 'Pernambuco e Nordeste'],
  ['America/Belem', 'Pará e Amapá'],
  ['America/Cuiaba', 'Mato Grosso'],
  ['America/Campo_Grande', 'Mato Grosso do Sul'],
  ['America/Manaus', 'Amazonas'],
  ['America/Porto_Velho', 'Rondônia'],
  ['America/Rio_Branco', 'Acre'],
  ['America/Noronha', 'Fernando de Noronha'],
] as const;

interface Form {
  name: string;
  volume: number;
  crossfadeS: number;
  minOfferGapMin: number;
  timezone: string;
}

const formFrom = (detail: StoreDetail): Form => ({
  name: detail.status.name,
  volume: detail.volume,
  crossfadeS: detail.crossfadeS,
  minOfferGapMin: Math.round(detail.minOfferGapS / 60),
  timezone: detail.timezone,
});

const sameForm = (a: Form, b: Form) =>
  a.name.trim() === b.name.trim() &&
  a.volume === b.volume &&
  a.crossfadeS === b.crossfadeS &&
  a.minOfferGapMin === b.minOfferGapMin &&
  a.timezone === b.timezone;

/** Aba "Ajustes": configurações da loja e manutenção (seção 7.2). */
export function StoreSettingsTab({ detail }: { detail: StoreDetail }) {
  const store = detail.status;
  const saved = formFrom(detail);
  const [form, setForm] = useState<Form>(saved);
  const update = useUpdateStore(store.id);
  const toast = useToast();
  const dirty = !sameForm(form, saved);
  const leaveDialog = useUnsavedChanges(dirty, 'Os ajustes da loja foram alterados e ainda não foram salvos.');

  const set = <K extends keyof Form>(key: K, value: Form[K]) => setForm((f) => ({ ...f, [key]: value }));
  const valid =
    form.name.trim().length > 0 &&
    inRange(form.volume, 0, 100) &&
    inRange(form.crossfadeS, 0, 30) &&
    inRange(form.minOfferGapMin, 0, 1440);

  const submit = async () => {
    try {
      const result = await update.mutateAsync({
        name: form.name.trim(),
        volume: form.volume,
        crossfadeS: form.crossfadeS,
        minOfferGapS: form.minOfferGapMin * 60,
        timezone: form.timezone,
      });
      setForm(formFrom(result));
      toast.success(
        store.online
          ? 'Ajustes salvos. A loja aplica em instantes.'
          : 'Ajustes salvos. A loja aplica quando se conectar.',
      );
    } catch (error) {
      toast.error(errorMessage(error));
    }
  };

  return (
    <div className="grid items-start gap-6 xl:grid-cols-2">
      <Card>
        <CardHeader title="Configurações da loja" />
        <form
          className="space-y-4 px-5 py-4"
          onSubmit={(e) => {
            e.preventDefault();
            if (dirty && valid) void submit();
          }}
        >
          <Field label="Nome">
            <Input value={form.name} maxLength={80} onChange={(e) => set('name', e.target.value)} required />
          </Field>
          <div className="grid gap-4 sm:grid-cols-2">
            <Field label="Volume padrão" hint="0 a 100. Vale ao iniciar; o volume ao vivo muda na aba Ao vivo.">
              <NumberInput value={form.volume} min={0} max={100} onChange={(v) => set('volume', v)} suffix="%" />
            </Field>
            <Field label="Mixagem entre músicas" hint="0 a 30 s. Padrão 5 s; 0 desliga a mixagem.">
              <NumberInput value={form.crossfadeS} min={0} max={30} onChange={(v) => set('crossfadeS', v)} suffix="s" />
            </Field>
            <Field
              label="Intervalo mínimo entre ofertas"
              hint={
                <span className="inline-flex items-center gap-1.5">
                  Padrão 5 min. Passa a valer com as ofertas <SoonTag stage={5} />
                </span>
              }
            >
              <NumberInput
                value={form.minOfferGapMin}
                min={0}
                max={1440}
                onChange={(v) => set('minOfferGapMin', v)}
                suffix="min"
              />
            </Field>
            <Field label="Fuso horário" hint="A agenda segue o horário deste fuso.">
              <Select value={form.timezone} onChange={(e) => set('timezone', e.target.value)}>
                {!timezones.some(([id]) => id === form.timezone) && (
                  <option value={form.timezone}>{form.timezone}</option>
                )}
                {timezones.map(([id, label]) => (
                  <option key={id} value={id}>
                    {label}
                  </option>
                ))}
              </Select>
            </Field>
          </div>
          <div className="flex justify-end gap-2 border-t border-stone-100 pt-4">
            {dirty && (
              <Button variant="ghost" onClick={() => setForm(saved)}>
                Desfazer
              </Button>
            )}
            <Button
              type="submit"
              variant="primary"
              icon={<Save className="size-4" />}
              busy={update.isPending}
              disabled={!dirty || !valid}
            >
              Salvar ajustes
            </Button>
          </div>
        </form>
      </Card>
      <Maintenance detail={detail} />
      {leaveDialog}
    </div>
  );
}

const inRange = (value: number, min: number, max: number) => Number.isInteger(value) && value >= min && value <= max;

function NumberInput({
  value,
  min,
  max,
  suffix,
  onChange,
}: {
  value: number;
  min: number;
  max: number;
  suffix: string;
  onChange: (value: number) => void;
}) {
  return (
    <div className="relative">
      <Input
        type="number"
        inputMode="numeric"
        min={min}
        max={max}
        step={1}
        value={Number.isNaN(value) ? '' : value}
        onChange={(e) => onChange(e.target.value === '' ? Number.NaN : Number(e.target.value))}
        className="pr-12"
        aria-invalid={!inRange(value, min, max)}
      />
      <span className="pointer-events-none absolute top-1/2 right-3 -translate-y-1/2 text-sm text-stone-400">
        {suffix}
      </span>
    </div>
  );
}

type Confirm = 'restart' | 'revoke' | 'delete' | null;

function Maintenance({ detail }: { detail: StoreDetail }) {
  const store = detail.status;
  const { generate, busy: generating, code, close } = useGenerateEnrollCode(store.id);
  const { run, busy } = useCommand(store.id);
  const revoke = useRevokeToken(store.id);
  const remove = useDeleteStore();
  const toast = useToast();
  const navigate = useNavigate();
  const [confirm, setConfirm] = useState<Confirm>(null);

  const doRevoke = async () => {
    try {
      await revoke.mutateAsync();
      toast.success('PC desvinculado. Gere um código novo para vincular de novo.');
    } catch (error) {
      toast.error(errorMessage(error));
    }
    setConfirm(null);
  };

  const doDelete = async () => {
    try {
      await remove.mutateAsync(store.id);
      toast.success(`Loja ${store.name} removida.`);
      void navigate('/lojas', { replace: true });
    } catch (error) {
      toast.error(errorMessage(error));
      setConfirm(null);
    }
  };

  return (
    <Card>
      <CardHeader title="Manutenção" />
      <div className="divide-y divide-stone-100">
        <Action
          title="Código de vinculação"
          description={
            store.enrolled
              ? 'Para trocar o PC da loja (formatado ou novo). O PC atual perde o acesso quando o código novo for usado.'
              : 'Informe o código na configuração local do PC da loja. Vale por 30 minutos.'
          }
        >
          <Button busy={generating} icon={<KeyRound className="size-4" />} onClick={() => void generate()}>
            Gerar código
          </Button>
        </Action>
        <Action
          title="Reiniciar agente"
          description="Reinicia o serviço de som no PC da loja. A música volta em alguns segundos."
        >
          <Button
            disabled={!store.online}
            busy={busy === 'restartAgent'}
            icon={<Power className="size-4" />}
            onClick={() => setConfirm('restart')}
          >
            Reiniciar
          </Button>
        </Action>
        <Action
          title={
            <span className="inline-flex items-center gap-2">
              Atualizar agente <SoonTag stage={6} />
            </span>
          }
          description={`Versão instalada: ${store.agentVersion ?? 'desconhecida'}. A atualização pelo painel chega na Etapa 6.`}
        >
          <Button disabled icon={<Download className="size-4" />}>
            Atualizar
          </Button>
        </Action>
        {store.enrolled && (
          <Action
            title="Desvincular o PC"
            description="O PC da loja deixa de falar com o Hub na hora, mas continua tocando o que já tem."
          >
            <Button variant="danger" icon={<Unlink className="size-4" />} onClick={() => setConfirm('revoke')}>
              Desvincular
            </Button>
          </Action>
        )}
        <Action
          title="Remover loja"
          description="Apaga a loja, a agenda e o histórico dela no Hub. Não dá para desfazer."
        >
          <Button variant="danger" icon={<Trash2 className="size-4" />} onClick={() => setConfirm('delete')}>
            Remover
          </Button>
        </Action>
      </div>

      {code && <EnrollCodeDialog code={code} replacing={store.enrolled} onClose={close} />}
      {confirm === 'restart' && (
        <ConfirmDialog
          title="Reiniciar o agente?"
          confirmLabel="Reiniciar"
          danger={false}
          busy={busy === 'restartAgent'}
          onCancel={() => setConfirm(null)}
          onConfirm={() => void run({ command: 'restartAgent' }).then(() => setConfirm(null))}
        >
          <p>A música da {store.name} para por alguns segundos enquanto o serviço reinicia.</p>
        </ConfirmDialog>
      )}
      {confirm === 'revoke' && (
        <ConfirmDialog
          title="Desvincular o PC da loja?"
          confirmLabel="Desvincular"
          busy={revoke.isPending}
          onCancel={() => setConfirm(null)}
          onConfirm={() => void doRevoke()}
        >
          <p>
            O PC da <strong>{store.name}</strong> perde o acesso ao Hub agora. Ele continua tocando a última
            configuração, mas não recebe alterações nem comandos até ser vinculado de novo com um código.
          </p>
        </ConfirmDialog>
      )}
      {confirm === 'delete' && (
        <ConfirmDialog
          title="Remover a loja?"
          confirmLabel="Remover loja"
          busy={remove.isPending}
          onCancel={() => setConfirm(null)}
          onConfirm={() => void doDelete()}
        >
          <p>
            A loja <strong>{store.name}</strong>, a agenda e o histórico dela serão apagados do Hub.
          </p>
          <p>
            O PC da loja é desconectado e continua tocando o que já tem até ser desinstalado ou vinculado a outra loja.
          </p>
        </ConfirmDialog>
      )}
    </Card>
  );
}

function Action({ title, description, children }: { title: ReactNode; description: string; children: ReactNode }) {
  return (
    <div className="flex flex-wrap items-center justify-between gap-3 px-5 py-4">
      <div className="min-w-0 flex-1">
        <p className="text-sm font-medium text-stone-900">{title}</p>
        <p className="mt-0.5 text-sm text-stone-500">{description}</p>
      </div>
      {children}
    </div>
  );
}
