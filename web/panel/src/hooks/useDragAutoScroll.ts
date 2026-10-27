import { useEffect } from 'react';

/** Faixa perto da borda de cima e de baixo da janela em que a página rola durante o arraste. */
export const AutoScrollEdge = 100;

/** Rolagem por quadro (px) com o cursor encostado na borda; diminui conforme ele se afasta. */
export const AutoScrollMaxStep = 22;

/**
 * Rola a página enquanto algo do tipo `type` é arrastado (arraste nativo do HTML) com o cursor perto da borda de
 * cima ou de baixo da janela. Durante esse arraste o navegador quase não rola sozinho e a roda do mouse não funciona,
 * então a oferta não chegava à linha do tempo quando a lista de ofertas estava mais para baixo na tela.
 */
export function useDragAutoScroll(type: string) {
  useEffect(() => {
    let cursorY: number | null = null;
    let frame = 0;

    const step = () => {
      frame = 0;
      if (cursorY === null) return;
      const height = window.innerHeight;
      let delta = 0;
      if (cursorY < AutoScrollEdge) delta = -AutoScrollMaxStep * (1 - Math.max(0, cursorY) / AutoScrollEdge);
      else if (cursorY > height - AutoScrollEdge)
        delta = AutoScrollMaxStep * (1 - Math.max(0, height - cursorY) / AutoScrollEdge);
      if (delta !== 0) window.scrollBy(0, Math.round(delta) || Math.sign(delta));
      // Com o cursor parado o navegador manda dragover só de vez em quando; o laço segue rolando até soltar.
      frame = requestAnimationFrame(step);
    };

    const onDragOver = (event: DragEvent) => {
      if (!event.dataTransfer?.types.includes(type)) return;
      cursorY = event.clientY;
      if (!frame) frame = requestAnimationFrame(step);
    };

    const stop = () => {
      cursorY = null;
      if (frame) cancelAnimationFrame(frame);
      frame = 0;
    };

    document.addEventListener('dragover', onDragOver);
    document.addEventListener('drop', stop);
    document.addEventListener('dragend', stop);
    return () => {
      document.removeEventListener('dragover', onDragOver);
      document.removeEventListener('drop', stop);
      document.removeEventListener('dragend', stop);
      stop();
    };
  }, [type]);
}
