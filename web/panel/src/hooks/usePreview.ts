import { useEffect, useRef, useState } from 'react';

/** Pré-escuta no navegador (seção 7.2): um áudio por vez, direto do Hub (aceita Range, então dá para avançar). */
export function usePreview() {
  const audio = useRef<HTMLAudioElement | null>(null);
  const [playing, setPlaying] = useState<string | null>(null);

  useEffect(() => {
    const element = new Audio();
    element.preload = 'none';
    element.onended = () => setPlaying(null);
    element.onpause = () => setPlaying((current) => (element.ended || element.paused ? null : current));
    audio.current = element;
    return () => {
      element.pause();
      element.src = '';
    };
  }, []);

  const toggle = (hash: string) => {
    const element = audio.current;
    if (!element) return;
    if (playing === hash) {
      element.pause();
      setPlaying(null);
      return;
    }
    element.src = `/api/tracks/${hash}/audio`;
    void element.play().then(
      () => setPlaying(hash),
      () => setPlaying(null),
    );
  };

  return { playing, toggle };
}
