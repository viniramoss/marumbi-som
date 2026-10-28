import { Megaphone } from 'lucide-react';
import { Card, EmptyState, PageHeader, SoonTag } from '../../components/ui';

/** A tela de Ofertas é entrega da Etapa 5 (D23 a). */
export function OffersPage() {
  return (
    <>
      <PageHeader
        title="Ofertas"
        description="Anúncios gravados que tocam entre ou durante as músicas, com a música abaixada."
      />
      <Card>
        <EmptyState icon={<Megaphone className="size-10" />} title="Chega na próxima etapa">
          <p className="mb-3">
            <SoonTag stage={5} />
          </p>
          <p>
            Aqui você vai enviar o áudio da oferta, escolher a validade, as lojas e quando ela toca (a cada N músicas, a
            cada X minutos, em horário fixo ou presa a uma música da playlist). O agente das lojas já sabe tocar
            ofertas; falta a tela.
          </p>
        </EmptyState>
      </Card>
    </>
  );
}
