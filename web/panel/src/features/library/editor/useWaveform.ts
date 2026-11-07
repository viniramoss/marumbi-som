import { useEffect, useState } from 'react';

/** Desenho da onda: o pico de cada pedaço de {@link BucketMs} ms, de 0 a 1. */
export interface Waveform {
  peaks: Float32Array;
  durationMs: number;
}

export const BucketMs = 10;

export type WaveformState =
  | { status: 'loading'; progress: number | null }
  | { status: 'decoding' }
  | { status: 'ready'; waveform: Waveform }
  | { status: 'error'; message: string };

/**
 * Baixa o arquivo da biblioteca (com andamento) e calcula a onda no navegador (D34): o Hub não precisa de nada novo
 * e o desenho é exatamente o do arquivo que as lojas tocam.
 */
export function useWaveform(hash: string): WaveformState {
  // Guardado com o hash, para outra música começar de novo em "baixando" sem limpar o estado dentro do efeito.
  const [entry, setEntry] = useState<{ hash: string; state: WaveformState }>({
    hash,
    state: { status: 'loading', progress: 0 },
  });

  useEffect(() => {
    const abort = new AbortController();
    const set = (state: WaveformState) => {
      if (!abort.signal.aborted) setEntry({ hash, state });
    };
    void (async () => {
      try {
        const response = await fetch(`/api/tracks/${hash}/audio`, { signal: abort.signal, credentials: 'same-origin' });
        if (!response.ok) throw new Error(`O Hub não entregou o áudio (${response.status}).`);
        const data = await readWithProgress(response, (progress) => set({ status: 'loading', progress }));
        set({ status: 'decoding' });
        const context = new OfflineAudioContext(1, 1, 44_100);
        const buffer = await context.decodeAudioData(data);
        set({ status: 'ready', waveform: computePeaks(buffer) });
      } catch (error) {
        // EncodingError: navegador sem o decodificador de AAC (o Chrome e o Edge têm).
        const message =
          error instanceof DOMException && error.name === 'EncodingError'
            ? 'Este navegador não consegue abrir o áudio da música. Use o Chrome ou o Edge atualizados.'
            : error instanceof Error && error.message
              ? error.message
              : 'Não foi possível abrir o áudio.';
        set({ status: 'error', message });
      }
    })();
    return () => abort.abort();
  }, [hash]);

  return entry.hash === hash ? entry.state : { status: 'loading', progress: 0 };
}

async function readWithProgress(response: Response, onProgress: (progress: number | null) => void) {
  const total = Number(response.headers.get('Content-Length')) || 0;
  const reader = response.body?.getReader();
  if (!reader) return response.arrayBuffer();
  const chunks: Uint8Array[] = [];
  let received = 0;
  for (;;) {
    const { done, value } = await reader.read();
    if (done) break;
    chunks.push(value);
    received += value.length;
    onProgress(total > 0 ? received / total : null);
  }
  const data = new Uint8Array(received);
  let offset = 0;
  for (const chunk of chunks) {
    data.set(chunk, offset);
    offset += chunk.length;
  }
  return data.buffer;
}

export function computePeaks(
  buffer: Pick<AudioBuffer, 'sampleRate' | 'length' | 'numberOfChannels' | 'getChannelData'>,
): Waveform {
  const perBucket = Math.max(1, Math.round((buffer.sampleRate * BucketMs) / 1000));
  const peaks = new Float32Array(Math.ceil(buffer.length / perBucket));
  for (let channel = 0; channel < buffer.numberOfChannels; channel++) {
    const samples = buffer.getChannelData(channel);
    for (let i = 0; i < samples.length; i++) {
      const value = Math.abs(samples[i] ?? 0);
      const bucket = (i / perBucket) | 0;
      if (value > (peaks[bucket] ?? 0)) peaks[bucket] = value;
    }
  }
  return { peaks, durationMs: Math.round((buffer.length / buffer.sampleRate) * 1000) };
}
