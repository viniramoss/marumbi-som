import { Copy, KeyRound, Link2 } from 'lucide-react';
import type { EnrollCodeResult, StoreSummary } from '../../api/types';
import { Modal } from '../../components/Modal';
import { useToast } from '../../components/Toasts';
import { Button, Card, CardHeader } from '../../components/ui';
import { formatTime } from '../../lib/format';
import { useGenerateEnrollCode } from './useGenerateEnrollCode';

/** Aba "Ao vivo" de uma loja que ainda não tem PC vinculado. */
export function EnrollCard({ store }: { store: StoreSummary }) {
  const { generate, busy, code, close } = useGenerateEnrollCode(store.id);
  return (
    <Card className="max-w-2xl">
      <CardHeader
        title={
          <span className="inline-flex items-center gap-2">
            <Link2 className="size-4 text-stone-400" /> Vincular o PC da loja
          </span>
        }
      />
      <div className="space-y-4 px-5 py-4 text-sm text-stone-700">
        <p>Esta loja ainda não tem um PC vinculado. Para vincular:</p>
        <EnrollSteps />
        <Button variant="primary" busy={busy} icon={<KeyRound className="size-4" />} onClick={() => void generate()}>
          Gerar código de vinculação
        </Button>
      </div>
      {code && <EnrollCodeDialog code={code} replacing={false} onClose={close} />}
    </Card>
  );
}

function EnrollSteps() {
  return (
    <ol className="list-decimal space-y-1 pl-5">
      <li>Gere o código aqui. Ele vale por 30 minutos e só pode ser usado uma vez.</li>
      <li>
        No PC da loja, entre com a conta de administrador e abra <strong>Configurar Som</strong> na área de trabalho.
      </li>
      <li>
        Informe o endereço do Hub{' '}
        <code className="rounded bg-stone-100 px-1 py-0.5 text-xs">{window.location.origin}</code> e o código.
      </li>
      <li>A loja aparece como conectada aqui em poucos segundos e baixa a configuração.</li>
      <li>
        Monte a <strong>Agenda</strong> da loja (dias, horários e playlist): sem nenhuma faixa de horário ela não toca
        nada.
      </li>
    </ol>
  );
}

export function EnrollCodeDialog({
  code,
  replacing,
  onClose,
}: {
  code: EnrollCodeResult;
  replacing: boolean;
  onClose: () => void;
}) {
  const toast = useToast();
  const copy = async () => {
    try {
      await navigator.clipboard.writeText(code.code);
      toast.success('Código copiado.');
    } catch {
      toast.error('Não deu para copiar. Anote o código.');
    }
  };

  return (
    <Modal
      title="Código de vinculação"
      onClose={onClose}
      footer={
        <Button variant="primary" onClick={onClose}>
          Fechar
        </Button>
      }
    >
      <div className="space-y-4 text-sm text-stone-700">
        <div className="flex items-center justify-center gap-3 rounded-xl bg-stone-100 py-5">
          <span className="font-mono text-3xl font-semibold tracking-[0.25em] text-stone-900" data-testid="enroll-code">
            {code.code}
          </span>
          <Button
            size="sm"
            variant="ghost"
            aria-label="Copiar código"
            title="Copiar código"
            icon={<Copy className="size-4" />}
            onClick={() => void copy()}
          />
        </div>
        <p className="text-center text-stone-500">Vale até {formatTime(code.expiresAt)} e só pode ser usado uma vez.</p>
        {replacing && (
          <p className="rounded-lg bg-amber-50 px-3 py-2 text-amber-900 ring-1 ring-amber-200">
            Quando este código for usado, o PC vinculado hoje perde o acesso ao Hub.
          </p>
        )}
        <EnrollSteps />
      </div>
    </Modal>
  );
}
