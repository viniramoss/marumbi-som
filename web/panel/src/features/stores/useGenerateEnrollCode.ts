import { useState } from 'react';
import { errorMessage } from '../../api/client';
import { useEnrollCode } from '../../api/queries';
import type { EnrollCodeResult } from '../../api/types';
import { useToast } from '../../components/Toasts';

/** Gera o código de vinculação (seção 8.5) e mostra o diálogo com ele. */
export function useGenerateEnrollCode(storeId: number) {
  const enroll = useEnrollCode(storeId);
  const toast = useToast();
  const [code, setCode] = useState<EnrollCodeResult | null>(null);

  const generate = async () => {
    try {
      setCode(await enroll.mutateAsync());
    } catch (error) {
      toast.error(errorMessage(error));
    }
  };

  return { generate, busy: enroll.isPending, code, close: () => setCode(null) };
}
