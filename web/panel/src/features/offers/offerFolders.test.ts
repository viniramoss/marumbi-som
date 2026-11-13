import { inFolder, isGeneral, offersForPlaylist, parseFolder, storeIdsForFolder, storeNames } from './offerFolders';

const stores = [
  { id: 1, name: 'Loja 1' },
  { id: 2, name: 'Loja 2' },
  { id: 3, name: 'Loja 3' },
];
const geral = { id: 10, storeIds: [] };
const todasMarcadas = { id: 11, storeIds: [1, 2, 3] };
const loja1 = { id: 12, storeIds: [1] };
const loja3 = { id: 13, storeIds: [3] };
const lojas1e3 = { id: 14, storeIds: [1, 3] };
const offers = [geral, todasMarcadas, loja1, loja3, lojas1e3];

describe('pastas de ofertas (D40)', () => {
  it('sem loja ou com todas marcadas é Geral', () => {
    expect(isGeneral(geral, stores)).toBe(true);
    expect(isGeneral(todasMarcadas, stores)).toBe(true);
    expect(isGeneral(lojas1e3, stores)).toBe(false);
  });

  it('a oferta de duas lojas aparece nas duas pastas, e a Geral só na Geral', () => {
    const ids = (folder: Parameters<typeof inFolder>[1]) =>
      offers.filter((o) => inFolder(o, folder, stores)).map((o) => o.id);
    expect(ids('todas')).toEqual([10, 11, 12, 13, 14]);
    expect(ids('geral')).toEqual([10, 11]);
    expect(ids(1)).toEqual([12, 14]);
    expect(ids(2)).toEqual([]);
    expect(ids(3)).toEqual([13, 14]);
  });

  it('playlist geral mostra todas; a exclusiva mostra a Geral e as da loja', () => {
    expect(offersForPlaylist(offers, [], stores).map((o) => o.id)).toEqual([10, 11, 12, 13, 14]);
    expect(offersForPlaylist(offers, [1], stores).map((o) => o.id)).toEqual([10, 11, 12, 14]);
    expect(offersForPlaylist(offers, [2], stores).map((o) => o.id)).toEqual([10, 11]);
    expect(offersForPlaylist(offers, [2, 3], stores).map((o) => o.id)).toEqual([10, 11, 13, 14]);
  });

  it('lê a pasta do endereço e sugere as lojas da oferta nova', () => {
    expect(parseFolder('geral', stores)).toBe('geral');
    expect(parseFolder('3', stores)).toBe(3);
    expect(parseFolder('9', stores)).toBe('todas');
    expect(parseFolder(null, stores)).toBe('todas');
    expect(storeIdsForFolder(3, stores)).toEqual([3]);
    expect(storeIdsForFolder('geral', stores)).toEqual([1, 2, 3]);
    expect(storeNames([1, 3], stores)).toBe('Loja 1 e Loja 3');
  });
});
