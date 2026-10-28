# Etapa 3 — Hub e sincronização

Objetivo (seção 15): o Hub no PC do escritório guarda a biblioteca, processa as músicas, monta a configuração
de cada loja e a entrega pelo Tailscale; o agente da loja baixa, confere e aplica sem cortar a música.

## O que esta versão faz

- **Hub** (seção 6), serviço Windows `MarumbiSom.Hub`, só em `127.0.0.1:5080`:
  - banco SQLite em `C:\MarumbiSom\Hub\data\marumbi.db`, com backup diário às 02:00 (30 dias);
  - **ingestão** com ffmpeg: corta silêncio do início e do fim, fade-in de 0,5 s e fade-out de 3 s, volume
    normalizado em −16 LUFS (ofertas em −14 LUFS, sem fade), AAC 160 kbps; duplicatas são detectadas pelo
    hash;
  - playlists, lojas, horários e o **manifesto** de cada loja, regenerado a cada mudança e todo dia às 00:05;
  - API do painel (seção 11.2) com login, e API das lojas: vinculação por código, manifesto, arquivos com
    retomada e WebSocket para avisos e comandos ao vivo.
- **Agente** (seção 8.3):
  - vincula a loja pela página **Configurar Som** → seção **Hub** (endereço + código);
  - recebe o aviso de versão nova pelo WebSocket (polling de 60 s como garantia);
  - baixa um arquivo por vez, com limite de 2 Mbps, retomando de onde parou se a rede cair;
  - confere o SHA-256 de cada arquivo e só troca a configuração com 100% dos arquivos conferidos, no fim da
    música atual;
  - arquivo do cache corrompido ou apagado é baixado de novo; sem Hub, a loja segue tocando o que tem;
  - envia status a cada 30 s e responde comandos ao vivo (volume, pular, pausar, reconectar caixa).

O painel visual é a Etapa 4. Até lá, o `scripts\hub-teste.ps1` faz as operações de teste pela API.

## Instalar o Hub (PC do escritório, PowerShell como administrador)

1. Instale o Tailscale e faça login com a conta do administrador.
2. Baixe o artefato **MarumbiSom.Hub-win-x64** da última execução do CI deste PR em *Actions* e extraia,
   por exemplo, em `C:\MarumbiSom-hub-pacote`.
3. Rode:

```powershell
Set-ExecutionPolicy -Scope Process Bypass
cd C:\MarumbiSom-hub-pacote
.\scripts\install-hub.ps1 -SourceDir . -TailscaleServe
```

- Na primeira vez o instalador pede a **senha do painel** (usuário `admin`, mínimo 10 caracteres). Para
  trocar: `.\scripts\install-hub.ps1 -SourceDir . -ResetAdminPassword`.
- O instalador ajusta o plano de energia para não suspender na tomada (`-SkipPowerSettings` para não mexer).
- `-TailscaleServe` publica o Hub em `https://<nome-do-pc>.<sua-tailnet>.ts.net`. Esse endereço aparece em
  `tailscale serve status` e é o que vai na página Configurar Som das lojas.
- O serviço roda com a conta virtual `NT SERVICE\MarumbiSom.Hub` (sem administrador); a pasta
  `C:\MarumbiSom\Hub` fica acessível só para SYSTEM, Administradores e o próprio serviço.

## Atualizar o agente da loja

Esta versão do agente é a primeira que sabe falar com o Hub. Instale o artefato **MarumbiSom.Agent-win-x64**
deste PR como na Etapa 2 (`.\scripts\install-agent.ps1 -SourceDir .`); a configuração e a senha local são
mantidas.

## Montar uma loja de teste (no PC do Hub)

```powershell
cd C:\MarumbiSom-hub-pacote\scripts
.\hub-teste.ps1 enviar -Pasta C:\musicas-teste      # envia e processa; mostra o volume original de cada uma
.\hub-teste.ps1 montar -Loja "Loja 1 - Matriz"      # playlist "Teste" o dia todo + código de vinculação
```

No PC da loja: atalho **Configurar Som** → seção **Hub** → endereço `https://…ts.net` + código → **Vincular
esta loja**. A seção mostra o andamento ("Baixando a versão N: x de y arquivos"); quando termina, a música da
playlist "Teste" entra no fim da música que está tocando.

Outras ações: `inverter` (inverte a ordem), `atualizar` (põe na playlist as músicas novas da biblioteca),
`status` (lojas conectadas, versão aplicada, caixa e volume), `volume -Valor 40` (comando ao vivo) e `codigo`
(novo código de vinculação).

## Critérios de aceite (seção 15, Etapa 3)

| # | Teste | Como fazer | Meta | Resultado |
|---|---|---|---|---|
| 1 | Ingestão | `hub-teste.ps1 enviar` com 20 músicas em formatos variados (mp3, m4a, flac, wav, ogg…), algumas bem baixas e outras bem altas | Todas `OK` (ou `Duplicada`); no PC da loja todas tocam com o mesmo volume percebido | |
| 2 | Fade-out | Envie uma música cortada no meio (ex.: recortada no Audacity) | Termina com fade de 3 s, sem corte seco | |
| 3 | Ordem nova | `hub-teste.ps1 inverter` | A loja aplica em ≤ 60 s (normalmente 1–2 s), sem cortar a música atual; a próxima segue a ordem nova | |
| 4 | Músicas novas | Envie mais 10 músicas e rode `hub-teste.ps1 atualizar` | Configurar Som mostra "Baixando… x de 10"; a troca só acontece depois dos 10 | |
| 5 | Rede caída no download | Durante o teste 4, tire o cabo de rede (ou desligue o Wi-Fi) da loja por 1 min e volte | O download continua de onde parou (log: `Download de … retomado a partir de N bytes`); a música não para | |
| 6 | Cache corrompido | Pare o serviço, abra um `.m4a` qualquer de `C:\ProgramData\MarumbiSom\cache` no Bloco de Notas, apague um pedaço, salve e inicie o serviço | Log: `Arquivo do cache corrompido … apagado` e depois `Baixado e conferido`; a música pula só aquela faixa enquanto isso | |
| 7 | Hub desligado 24 h | Desligue o PC do Hub por 24 h (ou pare o serviço `MarumbiSom.Hub`) | A loja toca normalmente o tempo todo; Configurar Som mostra "Sem conexão ao vivo com o Hub" e volta sozinha quando o Hub liga | |
| 8 | Comando ao vivo | `hub-teste.ps1 volume -Valor 40` e depois `-Valor 70` | O volume muda suave em ~1 s | |
| 9 | Testes automatizados | CI do PR | Ingestão real com ffmpeg, retomada, SHA-256, troca só com 100%, Hub fora do ar, WebSocket | ✅ |

Registros úteis no log da loja: `Conectado ao Hub`, `Hub avisou manifesto novo: versão N`,
`Manifesto versão N recebido; faltam X arquivo(s)`, `Baixado e conferido`, `Manifesto versão N aplicado`.
No Hub: `C:\MarumbiSom\Hub\logs\hub-AAAAMMDD.log`.

## Teste sem hardware (qualquer sistema operacional)

```bash
echo "senha-muito-forte" | dotnet run --project src/MarumbiSom.Hub -- --create-admin admin --Hub:DataDirectory=/tmp/hub
dotnet run --project src/MarumbiSom.Hub -- --Hub:DataDirectory=/tmp/hub          # http://127.0.0.1:5080
echo "senha-de-teste" | dotnet run --project src/MarumbiSom.Agent -- --set-local-password --Agent:DataDirectory=/tmp/som
dotnet run --project src/MarumbiSom.Agent -- --simulate --Agent:DataDirectory=/tmp/som   # http://127.0.0.1:47800
```

Para testes, a página Configurar Som aceita `http://127.0.0.1:5080` como endereço do Hub (fora do próprio
computador, só `https`). O ffmpeg precisa estar no PATH (ou em `tools/` ao lado do Hub).
