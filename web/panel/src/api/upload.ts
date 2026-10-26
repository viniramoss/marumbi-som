import { api, call, statusMessage } from './client';
import type { HubUpdateStatus, IngestionJob, ReleaseInfo } from './types';

export const acceptedAudio = ['.mp3', '.m4a', '.aac', '.flac', '.wav', '.ogg', '.opus', '.wma'];

export function isAudioFile(name: string): boolean {
  const dot = name.lastIndexOf('.');
  return dot >= 0 && acceptedAudio.includes(name.slice(dot).toLowerCase());
}

/**
 * Envia um arquivo para a biblioteca (música ou áudio de oferta) com andamento (o fetch não informa o progresso do envio, o XMLHttpRequest sim).
 * Devolve os trabalhos de ingestão criados pelo Hub.
 */
export function uploadTrack(
  file: File,
  onProgress: (fraction: number) => void,
  kind: 'music' | 'offer' = 'music',
): Promise<IngestionJob[]> {
  return new Promise((resolve, reject) => {
    const request = new XMLHttpRequest();
    request.open('POST', '/api/tracks');
    request.setRequestHeader('X-MarumbiSom', '1');
    request.responseType = 'json';
    request.upload.onprogress = (event) => {
      if (event.lengthComputable) onProgress(event.loaded / event.total);
    };
    request.onload = () => {
      if (request.status === 202) {
        onProgress(1);
        resolve(request.response as IngestionJob[]);
        return;
      }
      const body = request.response as { error?: string } | null;
      reject(new Error(body?.error ?? statusMessage(request.status)));
    };
    request.onerror = () => reject(new Error('Sem conexão com o Hub.'));
    const form = new FormData();
    // Áudio de oferta passa pelo perfil de ofertas do Hub (seção 6.5, D26 a).
    if (kind === 'offer') form.append('kind', 'offer');
    form.append('files', file, file.name);
    request.send(form);
  });
}

/**
 * Espera o Hub terminar de processar um envio (na fila → processando → pronto, repetido ou erro), consultando a fila
 * a cada segundo. `onUpdate` recebe cada situação nova, para a tela mostrar o andamento.
 */
export async function waitForIngestion(jobId: string, onUpdate: (job: IngestionJob) => void): Promise<IngestionJob> {
  for (;;) {
    const job = (await call(api.GET('/api/uploads'))).find((j) => j.id === jobId);
    if (!job) throw new Error('O envio sumiu da fila do Hub (ele reiniciou?). Envie o áudio de novo.');
    onUpdate(job);
    if (job.status !== 'queued' && job.status !== 'processing') return job;
    await new Promise((resolve) => setTimeout(resolve, 1000));
  }
}

/**
 * Envia uma versão do agente (o .zip e o .sig.json gerados pelo sign-release, D27 d) com andamento. O Hub confere o
 * SHA-256 e o conteúdo antes de aceitar.
 */
export function uploadRelease(
  packageFile: File,
  signatureFile: File,
  onProgress: (fraction: number) => void,
): Promise<ReleaseInfo> {
  return uploadPackage<ReleaseInfo>('/api/releases', packageFile, signatureFile, onProgress);
}

/**
 * Envia um pacote do Hub (o .zip e o .sig.json do sign-release -HubDir, D32 f) com andamento. O Hub confere o SHA-256,
 * a assinatura e o conteúdo antes de guardar; devolve a situação da atualização do Hub.
 */
export function uploadHubPackage(
  packageFile: File,
  signatureFile: File,
  onProgress: (fraction: number) => void,
): Promise<HubUpdateStatus> {
  return uploadPackage<HubUpdateStatus>('/api/hub/update/package', packageFile, signatureFile, onProgress);
}

function uploadPackage<T>(
  url: '/api/releases' | '/api/hub/update/package',
  packageFile: File,
  signatureFile: File,
  onProgress: (fraction: number) => void,
): Promise<T> {
  return new Promise((resolve, reject) => {
    const request = new XMLHttpRequest();
    request.open('POST', url);
    request.setRequestHeader('X-MarumbiSom', '1');
    request.responseType = 'json';
    request.upload.onprogress = (event) => {
      if (event.lengthComputable) onProgress(event.loaded / event.total);
    };
    request.onload = () => {
      if (request.status === 201) {
        onProgress(1);
        resolve(request.response as T);
        return;
      }
      const body = request.response as { error?: string } | null;
      reject(new Error(body?.error ?? statusMessage(request.status)));
    };
    request.onerror = () => reject(new Error('Sem conexão com o Hub.'));
    const form = new FormData();
    form.append('signature', signatureFile, signatureFile.name);
    form.append('package', packageFile, packageFile.name);
    request.send(form);
  });
}

/** Separa o .zip e o .sig.json escolhidos juntos; devolve o problema se faltar um dos dois. */
export function pickReleaseFiles(files: File[]): { packageFile: File; signatureFile: File } | string {
  const packageFile = files.find((f) => f.name.toLowerCase().endsWith('.zip'));
  const signatureFile = files.find((f) => f.name.toLowerCase().endsWith('.sig.json'));
  if (!packageFile || !signatureFile) {
    return 'Escolha os dois arquivos gerados pelo sign-release: o pacote .zip e a assinatura .sig.json.';
  }
  return { packageFile, signatureFile };
}
