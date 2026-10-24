import { readFileSync } from 'node:fs';
import { basename } from 'node:path';
import { expect, test, type Page } from '@playwright/test';

// Aceite das Etapas 4, 5 e 6 (seção 15): do upload à loja tocando, a oferta do cadastro ao histórico e a atualização
// assinada do agente, com o Hub real e um agente simulado conectado de verdade.
interface Stack {
  hubPort: number;
  agentPort: number;
  adminPassword: string;
  localPassword: string;
  tracks: string[];
  offerAudio: string;
  release: { version: string; zip: string; sig: string };
}

// Escrito pelo e2e/stack.mjs, que o Playwright sobe depois de carregar este arquivo.
let stack: Stack;

test.describe.configure({ mode: 'serial' });

let page: Page;
const problems: string[] = [];

test.beforeAll(async ({ browser }) => {
  stack = JSON.parse(readFileSync(new URL('./.stack.json', import.meta.url), 'utf8')) as Stack;
  page = await browser.newPage();
  // Erros no console (ex.: CSP bloqueando algo) reprovam o teste no fim.
  page.on('console', (message) => {
    if (message.type() === 'error') problems.push(message.text());
  });
  page.on('pageerror', (error) => problems.push(error.message));
});

test.afterAll(async () => {
  await page.close();
});

test('entra no painel', async () => {
  await page.goto('/');
  await expect(page).toHaveURL(/\/entrar/);
  await page.getByLabel('Usuário').fill('admin');
  await page.getByLabel('Senha').fill(stack.adminPassword);
  await page.getByRole('button', { name: 'Entrar' }).click();
  await expect(page.getByRole('heading', { name: 'Início' })).toBeVisible();
  await expect(page.getByText('Ao vivo').first()).toBeVisible();
  // O 401 de /api/auth/me antes de entrar é esperado (é assim que o painel sabe que precisa do login).
  problems.length = 0;
});

test('envia músicas e acompanha o processamento', async () => {
  await page.getByRole('link', { name: 'Biblioteca' }).first().click();
  // Por buffer: com caminhos que têm acento o Playwright perde o arquivo; o nome com acento chega igual ao Hub.
  await page
    .getByTestId('upload-input')
    .setInputFiles(
      stack.tracks.map((path) => ({ name: basename(path), mimeType: 'audio/mpeg', buffer: readFileSync(path) })),
    );
  const table = page.getByRole('table');
  await expect(table.getByText('Abertura do Açougue')).toBeVisible({ timeout: 60_000 });
  await expect(table.getByText('Fim de Tarde')).toBeVisible({ timeout: 60_000 });
  await expect(table.getByText('Trio Marumbi')).toBeVisible();
});

test('monta e publica uma playlist', async () => {
  await page.getByRole('link', { name: 'Playlists' }).first().click();
  await page.getByRole('button', { name: 'Nova playlist' }).click();
  await page.getByRole('dialog').getByLabel('Nome').fill('Manhã');
  await page.getByRole('dialog').getByRole('button', { name: 'Criar' }).click();
  await expect(page).toHaveURL(/\/playlists\/\d+/);

  await page.getByRole('button', { name: 'Adicionar músicas' }).click();
  const dialog = page.getByRole('dialog', { name: 'Adicionar músicas' });
  await dialog.getByText(/Marcar todas/).click();
  await dialog.getByRole('button', { name: 'Adicionar 2' }).click();
  const list = page.getByRole('list', { name: 'Músicas da playlist' });
  await expect(list.getByRole('listitem')).toHaveCount(2);

  await page.getByRole('button', { name: 'Salvar e publicar' }).click();
  await expect(page.getByText('Playlist salva.')).toBeVisible();
  await expect(page.getByText('alterações não salvas')).toHaveCount(0);
});

let enrollCode = '';

test('cadastra a loja e gera o código de vinculação', async () => {
  await page.getByRole('link', { name: 'Lojas' }).first().click();
  await page.getByRole('button', { name: 'Nova loja' }).click();
  await page.getByRole('dialog').getByLabel('Nome').fill('Marumbi Teste');
  await page.getByRole('dialog').getByRole('button', { name: 'Cadastrar' }).click();
  await expect(page.getByRole('heading', { name: 'Marumbi Teste' })).toBeVisible();
  await expect(page.getByText('Não vinculada').first()).toBeVisible();

  await page.getByRole('button', { name: 'Gerar código de vinculação' }).click();
  enrollCode = (await page.getByTestId('enroll-code').textContent())?.trim() ?? '';
  expect(enrollCode).toMatch(/^[A-Z0-9-]{8,}$/);
  await page.getByRole('dialog').getByRole('button', { name: 'Fechar' }).last().click();
});

test('monta a agenda na grade semanal', async () => {
  await page.getByRole('tab', { name: 'Agenda' }).click();
  await page.getByRole('button', { name: 'Adicionar faixa' }).click();
  await page.getByLabel('Playlist da faixa 1').selectOption({ label: 'Manhã' });
  await page.getByLabel('Início da faixa 1').fill('00:00');
  await page.getByLabel('Fim da faixa 1').fill('00:00');
  await page.getByRole('group', { name: 'Dias da faixa 1' }).getByRole('button', { name: 'Dom' }).click();
  await expect(page.getByTitle('Faixa 1: Manhã, 00:00–24:00')).toHaveCount(7);

  await page.getByRole('button', { name: 'Salvar agenda' }).click();
  await expect(page.getByText('Agenda salva. A loja aplica quando se conectar.')).toBeVisible();
});

test('vincula o PC da loja pela configuração local', async ({ browser }) => {
  const local = await browser.newPage();
  await local.goto(`http://127.0.0.1:${stack.agentPort}/`);
  await local.getByLabel('Senha da configuração local').fill(stack.localPassword);
  await local.getByRole('button', { name: 'Entrar' }).click();
  await local.getByLabel('Endereço do Hub').fill(`http://127.0.0.1:${stack.hubPort}`);
  await local.getByLabel(/Código de vinculação/).fill(enrollCode);
  await local.getByRole('button', { name: 'Vincular esta loja' }).click();
  await expect(local.getByText(/Loja vinculada: Marumbi Teste/)).toBeVisible();
  await local.close();
});

test('a loja aparece tocando ao vivo, sem recarregar a página', async () => {
  await page.getByRole('tab', { name: 'Ao vivo' }).click();
  await expect(page.getByRole('heading', { name: 'Marumbi Teste' })).toBeVisible();
  // Baixar, conferir e aplicar o manifesto leva alguns segundos no agente.
  await expect(page.getByText('Tocando', { exact: true }).first()).toBeVisible({ timeout: 60_000 });
  await expect(page.getByText('Aplicado', { exact: true })).toBeVisible({ timeout: 30_000 });
  await expect(page.getByText(/Abertura do Açougue|Fim de Tarde/).first()).toBeVisible();
});

test('comando ao vivo chega e volta em até 2 segundos', async () => {
  const started = Date.now();
  await page.getByRole('button', { name: 'Pausar' }).click();
  await expect(page.getByText('Pausada', { exact: true }).first()).toBeVisible({ timeout: 2_000 });
  expect(Date.now() - started).toBeLessThanOrEqual(2_000);

  await page.getByRole('button', { name: 'Tocar', exact: true }).click();
  await expect(page.getByText('Tocando', { exact: true }).first()).toBeVisible({ timeout: 2_000 });
});

test('o início mostra o cartão da loja e o histórico registra o que tocou', async () => {
  await page.getByRole('link', { name: 'Início' }).first().click();
  await expect(page.getByRole('link', { name: 'Marumbi Teste' })).toBeVisible();
  await expect(page.getByText('Sincronizado ✅')).toBeVisible();

  // O card inteiro abre a loja, não só o nome.
  await page.getByText('Sincronizado ✅').click();
  await expect(page.getByRole('heading', { name: 'Marumbi Teste' })).toBeVisible();
  await page.getByRole('tab', { name: 'Histórico' }).click();
  await expect(page.getByText(/Abertura do Açougue|Fim de Tarde/).first()).toBeVisible();
});

test('cadastra uma oferta com o áudio enviado na hora', async () => {
  await page.getByRole('link', { name: 'Ofertas' }).first().click();
  await page.getByRole('button', { name: 'Nova oferta' }).click();
  await expect(page).toHaveURL(/\/ofertas\/nova/);
  await page.getByTestId('offer-audio-input').setInputFiles({
    name: basename(stack.offerAudio),
    mimeType: 'audio/mpeg',
    buffer: readFileSync(stack.offerAudio),
  });
  await expect(page.getByText('Áudio pronto')).toBeVisible({ timeout: 60_000 });
  // O nome vem do arquivo; o administrador troca pelo texto da oferta.
  await expect(page.getByLabel('Nome da oferta')).toHaveValue('Oferta Picanha');
  await page.getByLabel('Nome da oferta').fill('Picanha R$ 69,90');
  await expect(page.getByLabel('Marumbi Teste')).toBeChecked();

  await page.getByRole('button', { name: 'Salvar e publicar' }).click();
  await expect(page.getByText('Oferta salva e publicada para as lojas escolhidas.')).toBeVisible();
  await expect(page).toHaveURL(/\/ofertas\/\d+$/);
  await expect(page.getByText('No ar', { exact: true })).toBeVisible();
  await expect(page.getByText('alterações não salvas')).toHaveCount(0);
});

test('prende a oferta numa música da playlist', async () => {
  await page.getByRole('link', { name: 'Playlists' }).first().click();
  await page.getByRole('link', { name: 'Manhã' }).click();
  await page
    .getByRole('button', { name: /^Prender oferta em / })
    .first()
    .click();
  const dialog = page.getByRole('dialog', { name: 'Prender oferta na música' });
  await dialog.getByLabel('Momento em que a oferta entra (minutos:segundos)').fill('0:10');
  await dialog.getByRole('button', { name: 'Prender' }).click();
  await expect(page.getByRole('button', { name: /Picanha R\$ 69,90\s*aos 0:10/ })).toBeVisible();

  await page.getByRole('button', { name: 'Salvar e publicar' }).click();
  await expect(page.getByText('Salva e publicada. As lojas trocam no fim da música atual.')).toBeVisible();
  await expect(page.getByText('alterações não salvas')).toHaveCount(0);
  await expect(page.getByText('1 oferta presa nesta playlist.')).toBeVisible();
});

test('toca a oferta agora pela loja e ela entra no histórico', async () => {
  await page.getByRole('link', { name: 'Lojas' }).first().click();
  await page.getByRole('link', { name: 'Marumbi Teste' }).click();
  await expect(page.getByText('Aplicado', { exact: true })).toBeVisible({ timeout: 30_000 });
  await expect(page.getByText('Tocando', { exact: true }).first()).toBeVisible({ timeout: 30_000 });

  await page.getByRole('button', { name: 'Tocar oferta agora…' }).click();
  const dialog = page.getByRole('dialog', { name: 'Tocar oferta agora' });
  await dialog.getByRole('button', { name: 'Tocar' }).click();
  await expect(page.getByText('A oferta entra agora, por cima da música.')).toBeVisible();

  // A oferta tem 8 s; o agente registra a execução e o painel mostra sem recarregar.
  await expect(page.getByText(/Última:\s*Picanha R\$ 69,90/)).toBeVisible({ timeout: 30_000 });
  await page.getByRole('tab', { name: 'Histórico' }).click();
  await expect(page.getByLabel('Oferta').first()).toBeVisible();
  await expect(page.getByText('Picanha R$ 69,90').first()).toBeVisible();
});

test('envia uma versão assinada do agente e atualiza a loja', async () => {
  await page.getByRole('link', { name: 'Configurações' }).first().click();
  await page.getByTestId('release-input').setInputFiles(
    [stack.release.zip, stack.release.sig].map((path) => ({
      name: basename(path),
      mimeType: path.endsWith('.zip') ? 'application/zip' : 'application/json',
      buffer: readFileSync(path),
    })),
  );
  await expect(page.getByText(`Versão ${stack.release.version} recebida e marcada para distribuição.`)).toBeVisible();
  await expect(page.getByText('Para distribuição', { exact: true })).toBeVisible();
  await expect(page.getByText(`1 loja conectada está numa versão anterior à ${stack.release.version}.`)).toBeVisible();

  await page.getByRole('link', { name: 'Lojas' }).first().click();
  await page.getByRole('link', { name: 'Marumbi Teste' }).click();
  await page.getByRole('tab', { name: 'Ajustes' }).click();
  await expect(page.getByText(`Para distribuição: ${stack.release.version}.`)).toBeVisible();
  await page.getByRole('button', { name: 'Atualizar', exact: true }).click();
  await page.getByRole('dialog').getByRole('button', { name: 'Atualizar' }).click();
  await expect(page.getByText('A loja começou a baixar a versão nova do agente.')).toBeVisible();
  // O agente confere a assinatura com a chave de teste; no modo simulado o Updater só registra o resultado.
  await expect(page.getByText(`Agente atualizado para ${stack.release.version}`)).toBeVisible({ timeout: 30_000 });
});

test('nenhum erro no console do navegador', () => {
  expect(problems).toEqual([]);
});
