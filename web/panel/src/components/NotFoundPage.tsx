import { Link } from 'react-router';
import { Button, Card, EmptyState } from './ui';

export function NotFoundPage() {
  return (
    <Card>
      <EmptyState title="Página não encontrada">
        <Link to="/">
          <Button variant="primary" className="mt-3">
            Voltar ao início
          </Button>
        </Link>
      </EmptyState>
    </Card>
  );
}
