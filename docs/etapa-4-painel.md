# Etapa 4 — Painel

Objetivo (seção 15): todas as telas da seção 7 no navegador, pensadas para o computador (D21), com o
dashboard e o controle ao vivo também no celular. O painel atualiza sozinho: nada exige recarregar a página.

## O que esta versão faz

O painel é servido pelo próprio Hub em `https://<pc-do-escritório>.<tailnet>.ts.net` (o mesmo endereço que
as lojas usam). Entra com o usuário `admin` e a senha definida no `install-hub.ps1`.

| Tela | O que tem |
|---|---|
| **Início** | Um cartão por loja: tocando agora com barra de progresso, caixa Bluetooth, configuração aplicada ("Sincronizado ✅" / "Baixando 3/10"), volume, último sinal, alertas e os botões tocar/pausar, pular e volume −/+ |
| **Lojas** | Lista com situação de cada loja e cadastro de loja nova |
| **Loja → Ao vivo** | Música atual, tocar/pausar, pular, volume (slider), "Tocar agora…" (escolhe uma música que já está no PC da loja), reconectar caixa, fila das próximas 10 e situação do PC (disco, versão do agente, horário) |
| **Loja → Agenda** | Faixas de horário (dias, início, fim, playlist) com a grade semanal ao lado; sobreposição no mesmo dia aparece na hora e impede salvar; fim 00:00 = meia-noite, fim antes do início atravessa a meia-noite |
| **Loja → Histórico** | O que tocou hoje ou nos últimos 7 dias |
| **Loja → Eventos** | Os 200 eventos mais recentes (caixa caiu/voltou, agente reiniciou, vinculação…) |
| **Loja → Ajustes** | Nome, volume padrão, mixagem entre músicas, intervalo entre ofertas, fuso; gerar código de vinculação, reiniciar agente, desvincular o PC e remover a loja (com confirmação) |
| **Biblioteca** | Envio de vários arquivos por arrastar ou escolher, com andamento por arquivo e situação do processamento; busca sem acento; pré-escuta; editar título e artista; remover (bloqueado se a música está em alguma playlist) |
| **Playlists** | Lista com lojas e horários que usam cada uma; editor com arrastar e soltar (funciona no toque), tempo acumulado, aleatório e "Salvar e publicar"; avisa antes de sair com alterações não salvas |
| **Configurações** | Trocar a senha, versão do Hub, disco livre e situação do backup |

Toda tela que mexe com a configuração mostra se cada loja afetada já aplicou ("Aplicado" ou o andamento).

Ficam para depois, marcados como "Etapa 5" ou "Etapa 6" no painel (D23 a): tela de Ofertas, coluna de ofertas
no editor de playlist, "tocar oferta agora", próxima/última oferta, distribuição e atualização do agente.
Pela D24, as ofertas da Etapa 5 tocam sempre por cima da música, com ducking.

Os alertas da seção 6.6 aparecem no cartão da loja, só dentro do horário de funcionamento (D21 e D23 e):
sem sinal há mais de 10 min, caixa desconectada há mais de 5 min, sincronização parada há mais de 60 min,
disco com menos de 1 GB e mais de 3 reinícios do agente em 1 h.

## Instalar ou atualizar o Hub

Use o artefato **MarumbiSom.Hub-win-x64** do CI deste PR, que agora traz o painel em `wwwroot`. A instalação
é igual à da Etapa 3:

```powershell
Set-ExecutionPolicy -Scope Process Bypass
cd C:\MarumbiSom-hub-pacote
.\scripts\install-hub.ps1 -SourceDir . -TailscaleServe
```

O instalador espelha `wwwroot` (arquivos de versões antigas do painel saem). O agente da loja não muda nesta
etapa além dos campos novos de status (fila e playlist, D23 b); instale o artefato **MarumbiSom.Agent-win-x64**
do mesmo CI para a fila aparecer na tela da loja.

## Critérios de aceite (seção 15, Etapa 4)

| # | Teste | Como fazer | Meta | Resultado |
|---|---|---|---|---|
| 1 | Todas as operações no computador | Pelo endereço do Tailscale: enviar músicas, montar uma playlist, cadastrar a loja, vincular com o código, montar a agenda, mudar ajustes, trocar a senha | Tudo pelo navegador, sem PowerShell | |
| 2 | Celular | Abrir o painel no celular (Tailscale ligado) | Início e Loja → Ao vivo utilizáveis em 360 px; tocar/pausar, pular e volume funcionam | |
| 3 | Comando ao vivo ≤ 2 s | Na loja tocando: pular, pausar, mudar o volume pelo slider | O painel mostra o estado novo em até 2 s | |
| 4 | Tempo real | Deixar o Início aberto; desligar a caixa Bluetooth da loja por 1 min e religar; trocar de música pelo PC da loja | Caixa e música mudam no cartão sozinhas, sem recarregar; o alerta de caixa só aparece depois de 5 min | |
| 5 | Salvar = publicar | Mudar a ordem de uma playlist em uso e "Salvar e publicar" | O painel mostra "Aplicado" para a loja em segundos; a loja troca no fim da música | |
| 6 | Agenda | Criar duas faixas que se sobrepõem num dia | O painel aponta as faixas e o dia e não deixa salvar | |
| 7 | Testes automatizados | CI do PR | Unitários do painel, testes do Hub (rotas, WebSocket, alertas, OpenAPI) e o teste ponta a ponta com Hub real, agente simulado e navegador (login → envio → playlist → loja → agenda → vínculo → loja tocando → pausar/tocar em ≤ 2 s) | ✅ |

## Desenvolvimento do painel

O código fica em `web/panel` (React 19, TypeScript, Vite, TanStack Query, Tailwind). Requer Node 22.

```bash
cd web/panel
npm ci
npm run dev        # http://localhost:5173, repassando /api e /ws para o Hub em http://127.0.0.1:5080
npm run lint       # ESLint + Prettier
npm test           # Vitest
npm run build      # tsc + vite build → dist/
npm run e2e        # sobe Hub e agente simulado (e2e/stack.mjs) e roda o Playwright; precisa de ffmpeg e do build
```

Os tipos da API vêm do OpenAPI do Hub (seção 7.3, D23 g): depois de mudar uma rota ou DTO, rode
`UPDATE_OPENAPI=1 dotnet test tests/MarumbiSom.Hub.Tests --filter OpenApi` (regrava `web/panel/openapi.json`)
e `npm run api` (regrava `src/types/api.ts`). O CI falha se qualquer um dos dois estiver desatualizado.

Para ver o painel servido pelo Hub, como em produção:

```bash
cd web/panel && npm run build && cd ../..
dotnet run --project src/MarumbiSom.Hub -- --webroot=$PWD/web/panel/dist --Hub:DataDirectory=/tmp/hub
```
