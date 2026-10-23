import { useRef, useState, type DragEvent } from 'react';
import { CircleAlert, CircleCheck, Copy, LoaderCircle, Upload } from 'lucide-react';
import { useQueryClient } from '@tanstack/react-query';
import { keys, useUploads } from '../../api/queries';
import type { IngestionJob, IngestionStatus } from '../../api/types';
import { acceptedAudio, isAudioFile, uploadTrack } from '../../api/upload';
import { Button, Card, CardHeader, ProgressBar } from '../../components/ui';

interface LocalUpload {
  key: string;
  name: string;
  progress: number;
  error?: string;
  jobId?: string;
}

const statusLook: Record<IngestionStatus, { text: string; tone: string; icon: 'spin' | 'ok' | 'warn' | 'dup' }> = {
  queued: { text: 'Na fila', tone: 'text-stone-500', icon: 'spin' },
  processing: { text: 'Processando (silêncio, fade, volume)…', tone: 'text-sky-700', icon: 'spin' },
  done: { text: 'Pronta', tone: 'text-emerald-700', icon: 'ok' },
  duplicate: { text: 'Já estava na biblioteca', tone: 'text-amber-700', icon: 'dup' },
  rejected: { text: 'Recusada', tone: 'text-red-700', icon: 'warn' },
  failed: { text: 'Erro', tone: 'text-red-700', icon: 'warn' },
};

const MaxParallel = 2;

/** Envio múltiplo por arrastar ou selecionar, com andamento por arquivo e situação do processamento (seção 7.2). */
export function UploadPanel() {
  const input = useRef<HTMLInputElement>(null);
  const [dragging, setDragging] = useState(false);
  const [local, setLocal] = useState<LocalUpload[]>([]);
  const uploads = useUploads();
  const client = useQueryClient();

  const jobs = new Map((uploads.data ?? []).map((job) => [job.id, job]));
  const mine = new Set(local.map((u) => u.jobId).filter(Boolean));
  // Também mostra o que está na fila vindo de outro aparelho.
  const others = (uploads.data ?? []).filter(
    (job) => !mine.has(job.id) && (job.status === 'queued' || job.status === 'processing'),
  );

  const update = (key: string, change: Partial<LocalUpload>) =>
    setLocal((list) => list.map((u) => (u.key === key ? { ...u, ...change } : u)));

  const start = async (files: File[]) => {
    const items = files.map((file) => ({
      file,
      key: `${file.name}-${file.size}-${Math.random().toString(36).slice(2)}`,
    }));
    setLocal((list) => [
      ...items.map(({ file, key }) =>
        isAudioFile(file.name)
          ? { key, name: file.name, progress: 0 }
          : { key, name: file.name, progress: 0, error: 'Formato não aceito.' },
      ),
      ...list,
    ]);
    const queue = items.filter(({ file }) => isAudioFile(file.name));
    const worker = async () => {
      for (let next = queue.shift(); next; next = queue.shift()) {
        const { file, key } = next;
        try {
          const created: IngestionJob[] = await uploadTrack(file, (progress) => update(key, { progress }));
          const job = created[0];
          update(key, job?.status === 'rejected' ? { error: job.error ?? 'Recusada.' } : { jobId: job?.id });
          client.setQueryData<IngestionJob[]>(keys.uploads, (list) => [...created, ...(list ?? [])]);
        } catch (error) {
          update(key, { error: error instanceof Error ? error.message : 'Falha no envio.' });
        }
      }
    };
    await Promise.all(Array.from({ length: MaxParallel }, worker));
    void client.invalidateQueries({ queryKey: keys.uploads });
  };

  const onDrop = (event: DragEvent) => {
    event.preventDefault();
    setDragging(false);
    const files = Array.from(event.dataTransfer.files);
    if (files.length > 0) void start(files);
  };

  const rows = [
    ...local.map((upload) => ({
      key: upload.key,
      name: upload.name,
      upload,
      job: upload.jobId ? jobs.get(upload.jobId) : undefined,
    })),
    ...others.map((job) => ({ key: job.id, name: job.originalName, upload: undefined, job })),
  ];
  const finished = rows.filter(
    (r) => r.upload?.error || (r.job && !['queued', 'processing'].includes(r.job.status)),
  ).length;

  return (
    <Card>
      <CardHeader
        title="Enviar músicas"
        subtitle="O Hub corta o silêncio, faz o fade, iguala o volume (−16 LUFS) e converte para AAC. Pode enviar vários de uma vez."
      />
      <div className="space-y-4 p-5">
        <div
          onDragOver={(event) => {
            event.preventDefault();
            setDragging(true);
          }}
          onDragLeave={() => setDragging(false)}
          onDrop={onDrop}
          className={`flex flex-col items-center justify-center gap-3 rounded-xl border-2 border-dashed px-6 py-8 text-center transition-colors ${
            dragging ? 'border-brand-600 bg-brand-50' : 'border-stone-300 bg-stone-50'
          }`}
        >
          <Upload className="size-8 text-stone-400" aria-hidden />
          <p className="text-sm text-stone-600">
            Arraste os arquivos para cá ou <span className="hidden sm:inline">use o botão</span>
          </p>
          <Button variant="primary" onClick={() => input.current?.click()} icon={<Upload className="size-4" />}>
            Escolher arquivos
          </Button>
          <p className="text-xs text-stone-500">{acceptedAudio.join(' ')} · até 100 MB cada</p>
          <input
            ref={input}
            type="file"
            multiple
            accept={acceptedAudio.join(',')}
            className="hidden"
            data-testid="upload-input"
            onChange={(event) => {
              const files = Array.from(event.target.files ?? []);
              event.target.value = '';
              if (files.length > 0) void start(files);
            }}
          />
        </div>

        {rows.length > 0 && (
          <div>
            <div className="mb-2 flex items-center justify-between text-sm">
              <span className="font-medium text-stone-700">
                Envios: {finished} de {rows.length} concluídos
              </span>
              {finished === rows.length && local.length > 0 && (
                <Button size="sm" variant="ghost" onClick={() => setLocal([])}>
                  Limpar lista
                </Button>
              )}
            </div>
            <ul className="divide-y divide-stone-100 rounded-lg ring-1 ring-stone-200">
              {rows.map(({ key, name, upload, job }) => (
                <UploadRow key={key} name={name} upload={upload} job={job} />
              ))}
            </ul>
          </div>
        )}
      </div>
    </Card>
  );
}

function UploadRow({ name, upload, job }: { name: string; upload?: LocalUpload; job?: IngestionJob }) {
  let content;
  if (upload?.error) {
    content = (
      <span className="flex items-center gap-1.5 text-red-700">
        <CircleAlert className="size-4" /> {upload.error}
      </span>
    );
  } else if (upload && !job && upload.progress < 1) {
    content = (
      <span className="flex w-40 items-center gap-2">
        <ProgressBar value={upload.progress * 100} max={100} label={`Enviando ${name}`} />
        <span className="w-9 text-right text-xs text-stone-500 tabular-nums">{Math.round(upload.progress * 100)}%</span>
      </span>
    );
  } else {
    const look = statusLook[job?.status ?? 'queued'];
    const icon =
      look.icon === 'spin' ? (
        <LoaderCircle className="size-4 animate-spin" />
      ) : look.icon === 'ok' ? (
        <CircleCheck className="size-4" />
      ) : look.icon === 'dup' ? (
        <Copy className="size-4" />
      ) : (
        <CircleAlert className="size-4" />
      );
    content = (
      <span className={`flex items-center gap-1.5 ${look.tone}`}>
        {icon} {look.text}
        {job?.error && <span>: {job.error}</span>}
      </span>
    );
  }
  return (
    <li className="flex flex-wrap items-center justify-between gap-x-4 gap-y-1 px-3 py-2 text-sm">
      <span className="min-w-0 flex-1 truncate text-stone-800" title={name}>
        {name}
      </span>
      {content}
    </li>
  );
}
