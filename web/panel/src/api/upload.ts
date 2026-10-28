import { statusMessage } from './client';
import type { IngestionJob } from './types';

export const acceptedAudio = ['.mp3', '.m4a', '.aac', '.flac', '.wav', '.ogg', '.opus', '.wma'];

export function isAudioFile(name: string): boolean {
  const dot = name.lastIndexOf('.');
  return dot >= 0 && acceptedAudio.includes(name.slice(dot).toLowerCase());
}

/**
 * Envia um arquivo para a biblioteca com andamento (o fetch não informa o progresso do envio, o XMLHttpRequest sim).
 * Devolve os trabalhos de ingestão criados pelo Hub.
 */
export function uploadTrack(file: File, onProgress: (fraction: number) => void): Promise<IngestionJob[]> {
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
    form.append('files', file, file.name);
    request.send(form);
  });
}
