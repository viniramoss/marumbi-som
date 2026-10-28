import { useEffect } from 'react';
import { useBlocker } from 'react-router';
import { ConfirmDialog } from '../components/Modal';

/**
 * Pergunta antes de sair com alterações não salvas: troca de tela, troca de aba (mudança no `?aba=`) e fechar a
 * página. Devolve o diálogo, que a tela coloca onde quiser.
 */
export function useUnsavedChanges(dirty: boolean, message: string) {
  const blocker = useBlocker(
    ({ currentLocation, nextLocation }) =>
      dirty && (currentLocation.pathname !== nextLocation.pathname || currentLocation.search !== nextLocation.search),
  );

  useEffect(() => {
    if (!dirty) return;
    const onUnload = (event: BeforeUnloadEvent) => event.preventDefault();
    window.addEventListener('beforeunload', onUnload);
    return () => window.removeEventListener('beforeunload', onUnload);
  }, [dirty]);

  if (blocker.state !== 'blocked') return null;
  return (
    <ConfirmDialog
      title="Sair sem salvar?"
      confirmLabel="Sair sem salvar"
      onCancel={() => blocker.reset()}
      onConfirm={() => blocker.proceed()}
    >
      <p>{message}</p>
    </ConfirmDialog>
  );
}
