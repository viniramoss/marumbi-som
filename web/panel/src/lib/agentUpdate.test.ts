import type { ReleaseInfo } from '../api/types';
import { pickReleaseFiles } from '../api/upload';
import { storeSummary } from '../test/fixtures';
import { compareVersions, storesToUpdate, updateBlocker } from './agentUpdate';

const release = (version: string): ReleaseInfo => ({
  version,
  sha256: 'a'.repeat(64),
  sizeBytes: 70_000_000,
  createdAt: '2026-10-24T10:00:00Z',
  isCurrent: true,
  storeCount: 0,
});

describe('atualização do agente (D27 h)', () => {
  it('compara versões pelos números, não pelo texto', () => {
    expect(compareVersions('0.6.9', '0.6.10')).toBe(-1);
    expect(compareVersions('0.6.10', '0.6.10')).toBe(0);
    expect(compareVersions('1.0.0', '0.9.99')).toBe(1);
    expect(compareVersions(null, '0.1.0')).toBe(-1);
  });

  it('só oferece atualizar lojas conectadas, mais antigas e sem atualização em andamento', () => {
    const current = release('0.6.12');
    const stores = [
      storeSummary({ id: 1, name: 'Antiga', agentVersion: '0.6.10' }),
      storeSummary({ id: 2, name: 'Em dia', agentVersion: '0.6.12' }),
      storeSummary({ id: 3, name: 'Desconectada', agentVersion: '0.6.10', online: false }),
      storeSummary({
        id: 4,
        name: 'Baixando',
        agentVersion: '0.6.10',
        update: { version: '0.6.12', stage: 'downloading', doneBytes: 1, totalBytes: 2, error: null, at: '' },
      }),
      storeSummary({
        id: 5,
        name: 'Revertida',
        agentVersion: '0.6.10',
        update: { version: '0.6.12', stage: 'rolledBack', doneBytes: null, totalBytes: null, error: 'x', at: '' },
      }),
    ];

    expect(storesToUpdate(stores, current).map((s) => s.name)).toEqual(['Antiga', 'Revertida']);
    expect(storesToUpdate(stores, undefined)).toEqual([]);
    expect(updateBlocker(stores[0]!, current)).toBeNull();
    expect(updateBlocker(stores[1]!, current)).toBe('A loja já está na versão 0.6.12.');
    expect(updateBlocker(stores[2]!, current)).toBe('A loja está desconectada do Hub.');
    expect(updateBlocker(stores[3]!, current)).toBe('Já há uma atualização em andamento.');
    expect(updateBlocker(stores[0]!, undefined)).toMatch(/Envie o pacote em Configurações/);
  });

  it('separa o pacote e a assinatura escolhidos juntos', () => {
    const zip = new File(['zip'], 'MarumbiSom.Agent-0.6.12.zip');
    const sig = new File(['{}'], 'MarumbiSom.Agent-0.6.12.sig.json');

    expect(pickReleaseFiles([sig, zip])).toEqual({ packageFile: zip, signatureFile: sig });
    expect(pickReleaseFiles([zip])).toMatch(/os dois arquivos/);
  });
});
