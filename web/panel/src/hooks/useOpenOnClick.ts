import type { MouseEvent } from 'react';
import { useNavigate } from 'react-router';

// Cliques nestes elementos (e em diálogos, cujos eventos sobem pelo portal) ficam com a ação deles.
const ownAction = 'a, button, input, select, textarea, label, [role="dialog"]';

/** Card ou linha inteira abre a tela, não só o nome. O nome continua sendo link, para teclado e "abrir em nova aba". */
export function useOpenOnClick() {
  const navigate = useNavigate();
  return (to: string) => (event: MouseEvent<HTMLElement>) => {
    if (event.target instanceof Element && event.target.closest(ownAction)) return;
    // Quem está selecionando texto (copiar o nome da música, por exemplo) não quer trocar de tela.
    if (window.getSelection()?.toString()) return;
    void navigate(to);
  };
}
