# Sistema de Som Remoto — Casa de Carnes Marumbi

> Documento de arquitetura e execução. Versão 1.8 — outubro/2026 (inclui ofertas com ducking, mixagem entre músicas e as decisões D17 a D26 da implementação).
> Este documento é a fonte da verdade do projeto. Toda implementação DEVE segui-lo. Qualquer desvio DEVE ser registrado na seção 18 (Registro de Decisões) antes de ser implementado.

---

## Sumário

1. Objetivo e escopo
2. Regras invioláveis
3. Visão geral da arquitetura
4. Decisões de tecnologia
5. Rede e conectividade (Tailscale)
6. Hub (backend do escritório)
7. Painel administrativo (frontend)
8. Agente da loja (Windows)
9. Guardião Bluetooth
10. Player, reprodução e ofertas
11. Protocolos: manifesto, API e WebSocket
12. Segurança
13. Estrutura do repositório e padrões de código
14. Divisão de responsabilidades: humano × agente de IA
15. Etapas de implementação e critérios de aceite
16. Testes e conferência
17. Operação, monitoramento e manutenção
18. Registro de decisões
19. Riscos conhecidos
20. Aspecto legal
21. Glossário
22. Fontes

---

## 1. Objetivo e escopo

### 1.1 Objetivo

Controlar, a partir do escritório (ou do celular), a música ambiente das lojas da Casa de Carnes Marumbi (Loja 1, Loja 2, Loja 3 e futuras), sem streaming contínuo, com:

- adicionar, remover e reordenar músicas por loja;
- transferência automática dos arquivos de música para cada loja;
- agenda de playlists por horário e dia da semana;
- controle ao vivo (tocar, pausar, pular, volume, tocar uma música agora);
- ofertas faladas (spots) entre músicas ou durante a música, com a música abaixada automaticamente (ducking);
- transição natural entre músicas, com fade e mixagem (crossfade), sem silêncio e sem cortes secos;
- conexão automática e permanente com uma caixa de som Bluetooth em cada loja;
- computador da loja sem controle do sistema pelos funcionários;
- custo zero em serviços.

### 1.2 Fora de escopo

- Streaming de áudio em tempo real do escritório para as lojas.
- Download de músicas de plataformas de streaming ou vídeo. O sistema SOMENTE ingere arquivos de áudio fornecidos pelo administrador.
- Múltiplas zonas de áudio dentro de uma mesma loja (fica para versão futura).
- Geração de locução por síntese de voz. Os áudios das ofertas são fornecidos prontos.

### 1.3 Ambiente

| Item | Situação |
|---|---|
| Computador das lojas | PC Windows já existente na loja, usado também por funcionários |
| Bluetooth das lojas | Adaptador USB |
| Caixa de som | Caixa Bluetooth (A2DP), uma por loja |
| Escritório | PC Windows do escritório, que roda o Hub |
| Acesso do administrador | PC do escritório e celular |
| Tolerância a falha | Instabilidade pontual é aceitável. Silêncio na loja não é crítico, mas o sistema DEVE se recuperar sozinho |

---

## 2. Regras invioláveis

Estas regras valem para todo o código, todos os scripts e todas as decisões. Nenhuma tarefa justifica violá-las.

1. **A loja NUNCA depende de rede para tocar.** Toda música tocada sai do cache local. Hub desligado, internet caída ou Tailscale fora DEVEM resultar em música tocando normalmente com a última configuração válida.
2. **NUNCA ativar um manifesto incompleto.** Uma nova configuração só é aplicada quando 100% dos arquivos referenciados estão no cache e com SHA-256 verificado.
3. **NUNCA interromper a música que está tocando por causa de sincronização.** Mudanças de fila entram a partir da próxima faixa. A única exceção é o comando explícito do administrador ("pular", "tocar agora", "pausar").
4. **NUNCA apagar do cache um arquivo referenciado** pelo manifesto ativo ou pelo manifesto pendente.
5. **O computador da loja NÃO abre nenhuma porta de entrada na rede.** O agente se conecta ao Hub (saída). Nada se conecta ao agente pela rede.
6. **O Hub NUNCA é exposto na internet pública.** Ele é acessível exclusivamente pela rede Tailscale.
7. **Todo arquivo de música é identificado pelo SHA-256 do seu conteúdo.** Nome de arquivo, título e artista são metadados, nunca identificadores.
8. **Toda atualização do agente DEVE ser assinada** (Ed25519) e verificada antes de executar. Pacote sem assinatura válida é descartado.
9. **Segredos NUNCA vão para o repositório Git.** Tokens, senhas, chaves privadas e chaves do Tailscale ficam fora do controle de versão.
10. **Somente serviços gratuitos.** Nenhuma dependência paga, nenhum trial, nenhum cartão de crédito obrigatório.
11. **O funcionário da loja NÃO consegue parar, fechar, desinstalar ou reconfigurar o sistema** sem a senha de administrador.
12. **Toda operação de escrita em arquivo de estado é atômica** (escreve em `.tmp` e substitui com `File.Replace`/`File.Move` com sobrescrita).
13. **Toda falha é registrada em log** com data/hora, componente e contexto. Nenhum `catch` vazio.
14. **Oferta fora da validade NUNCA toca.** O Hub não a envia, e o agente confere a validade com o relógio local antes de cada disparo.
15. **Ofertas NUNCA tocam fora das janelas de agenda da loja**, exceto pelo comando explícito "tocar oferta agora".
16. **NUNCA duas ofertas ao mesmo tempo, e NUNCA mudança de volume em degrau.** Toda alteração de volume da música causada por oferta é feita com rampa.

---

## 3. Visão geral da arquitetura

### 3.1 Princípio: estado desejado + cache local

O Hub não envia áudio. O Hub publica, para cada loja, um **manifesto versionado** que descreve o estado desejado: quais playlists, em que ordem, em que horários, com qual volume. O agente da loja compara o manifesto com o que tem no disco, baixa apenas o que falta, verifica, e só então aplica. É o mesmo modelo de reconciliação usado por ferramentas como Git e Dropbox.

Consequências:

- Reordenar uma playlist trafega poucos bytes.
- Cada música trafega uma única vez por loja (≈ 4 MB).
- Loja offline continua tocando; ao voltar, se atualiza sozinha.

### 3.2 Diagrama

```
                 ┌──────────────────── ESCRITÓRIO ────────────────────┐
                 │  PC do escritório (Windows)                        │
                 │                                                    │
   Admin ───────►│  Tailscale Serve (HTTPS :443)                      │
 (PC / celular)  │        │                                           │
                 │        ▼                                           │
                 │  MarumbiSom.Hub (ASP.NET Core, 127.0.0.1:5080)     │
                 │   ├─ API REST + WebSocket                          │
                 │   ├─ Painel (React, arquivos estáticos)            │
                 │   ├─ SQLite (estado)                               │
                 │   ├─ Biblioteca (arquivos .m4a por hash)           │
                 │   ├─ Ingestão (ffprobe/ffmpeg)                     │
                 │   └─ Alertas (bot Telegram)                        │
                 └─────────────────────────┬──────────────────────────┘
                                           │  Tailscale (WireGuard)
                                           │  conexões SEMPRE iniciadas pela loja
             ┌─────────────────────────────┼─────────────────────────────┐
             ▼                             ▼                             ▼
   ┌───── LOJA 1 (Windows) ─────┐  ┌───── LOJA 2 ─────┐        ┌───── LOJA 3 ─────┐
   │ Serviço MarumbiSom.Agent   │  │      (igual)     │        │      (igual)     │
   │  ├─ HubClient (WS + HTTP)  │  └──────────────────┘        └──────────────────┘
   │  ├─ SyncEngine + Cache     │
   │  ├─ Scheduler (agenda)     │
   │  ├─ Player (3× mpv)        │
   │  ├─ SpotScheduler (ofertas)│
   │  ├─ BluetoothGuardian      │
   │  ├─ AudioEndpointManager   │
   │  ├─ Telemetry              │
   │  ├─ LocalConfig (127.0.0.1)│
   │  └─ UpdateManager          │
   └────────────┬───────────────┘
                │ Bluetooth A2DP (adaptador USB)
               🔊 Caixa de som
```

### 3.3 Fluxo de uma alteração

1. Admin altera uma playlist no painel e salva.
2. Hub grava no SQLite e regenera o manifesto de cada loja afetada.
3. Se o conteúdo do manifesto mudou (comparação por hash), a versão da loja é incrementada.
4. Hub envia `manifest.changed` pelo WebSocket da loja.
5. Agente baixa o manifesto e calcula a diferença com o cache.
6. Agente baixa os arquivos faltantes (com retomada e limite de banda) e verifica o SHA-256 de cada um.
7. Com tudo completo, agente promove o manifesto pendente a ativo e reconstrói a fila a partir da próxima faixa.
8. Agente confirma (`manifest.applied`) e o painel mostra a loja como sincronizada.

Se o WebSocket estiver indisponível, o agente consulta a versão do manifesto a cada 60 segundos. O WebSocket é atalho, nunca dependência.

---

## 4. Decisões de tecnologia

| Camada | Escolha | Justificativa |
|---|---|---|
| Linguagem backend e agente | **C# / .NET 10 (LTS)** | Serviço Windows nativo, acesso direto às APIs de Bluetooth/áudio do Windows, contratos compartilhados entre Hub e agente, um único toolchain |
| Hub | ASP.NET Core (Minimal APIs) | Leve, WebSocket nativo, roda como serviço Windows |
| Banco | SQLite (modo WAL) + EF Core | Zero servidor, um arquivo, backup trivial |
| Frontend | React + Vite + TypeScript | Stack já conhecida pelo administrador |
| Estado/requisições no frontend | TanStack Query | Cache, revalidação e atualização otimista prontos |
| Arrastar e soltar | dnd-kit | Funciona com toque (celular) |
| Estilo | Tailwind CSS | Rápido, responsivo |
| Player | **mpv** (controlado por IPC via named pipe) | Gratuito, leve, saída de áudio direcionável a um dispositivo específico |
| Processamento de áudio | ffmpeg / ffprobe | Padrão da indústria, gratuito |
| Formato de áudio | **AAC-LC 160 kbps, 44,1 kHz, estéreo, `.m4a`** | Toca em qualquer navegador (inclusive iPhone) para pré-escuta, ótimo para Bluetooth, ≈ 4 MB por música |
| Rede | **Tailscale** (plano Personal gratuito) | VPN mesh sem abrir portas, HTTPS automático via Tailscale Serve |
| Alertas | Bot do Telegram | Gratuito, chega no celular |
| Logs | Serilog (arquivos rotativos) | Padrão .NET |
| Instalador | Inno Setup | Gratuito, gera `.exe` de instalação |
| Wrapper de serviço | Nativo .NET (`UseWindowsService()`) | Sem ferramenta extra |
| Repositório | GitHub (privado, gratuito) | Versionamento |
| Testes | xUnit (C#), Vitest (frontend) | Padrão |

---

## 5. Rede e conectividade (Tailscale)

### 5.1 Topologia

- **Hub:** dispositivo de usuário do administrador (não marcado com tag), com Tailscale Serve publicando o Hub em HTTPS.
- **Lojas:** dispositivos marcados com `tag:loja`.
- **Celular e PC do admin:** dispositivos de usuário.

### 5.2 Regras de acesso (ACL)

O arquivo de política DEVE implementar exatamente isto:

| Origem | Destino | Porta | Motivo |
|---|---|---|---|
| Dispositivos do admin | Hub | 443 | Painel |
| `tag:loja` | Hub | 443 | Agente → Hub |
| Dispositivos do admin | `tag:loja` | 3389 | Área de Trabalho Remota (manutenção emergencial) |
| `tag:loja` | `tag:loja` | — | **Proibido** |
| `tag:loja` | Dispositivos do admin (exceto Hub) | — | **Proibido** |

Modelo (o IP do Hub é preenchido na instalação):

```jsonc
{
  "tagOwners": {
    "tag:loja": ["autogroup:admin"]
  },
  "hosts": {
    "hub": "100.X.Y.Z"
  },
  "grants": [
    { "src": ["autogroup:member"], "dst": ["hub"],      "ip": ["443"] },
    { "src": ["tag:loja"],         "dst": ["hub"],      "ip": ["443"] },
    { "src": ["autogroup:member"], "dst": ["tag:loja"], "ip": ["3389"] }
  ]
}
```

### 5.3 Configurações obrigatórias do Tailscale

- **Expiração de chave desativada** no Hub e em todas as lojas. Chave expirada = loja desconectada até alguém ir lá.
- **Modo autônomo ("Run unattended") ativado** nas lojas, para o Tailscale funcionar antes de alguém fazer login no Windows.
- **MagicDNS e certificados HTTPS ativados** no console do Tailscale (necessário para o Tailscale Serve). Observação: com HTTPS ativado, o nome da máquina aparece em logs públicos de transparência de certificados. Use um nome neutro (ex.: `hub-escritorio`).
- **Tailscale Serve no Hub:** `tailscale serve --bg 5080` (publica `https://<hub>.<tailnet>.ts.net` → `http://127.0.0.1:5080`).

---

## 6. Hub (backend do escritório)

### 6.1 Hospedagem

- Roda como serviço Windows `MarumbiSom.Hub` no PC do escritório.
- Escuta **somente** em `127.0.0.1:5080`. O acesso externo é exclusivo pelo Tailscale Serve.
- Início: Automático. Recuperação: reiniciar após 10 s / 10 s / 60 s.
- O PC do escritório NÃO PODE suspender ou hibernar. Se ele estiver desligado, as lojas seguem tocando; apenas edições e comandos ficam indisponíveis.

### 6.2 Diretórios

```
C:\MarumbiSom\Hub\
  app\                 binários
  data\
    marumbi.db         SQLite
    library\ab\abcdef…1234.m4a     biblioteca processada (2 primeiros caracteres do hash como subpasta)
    originals\         arquivos originais enviados (para reprocessar se necessário)
    releases\          pacotes assinados do agente
    backups\           backups diários do banco
  logs\
```

### 6.3 Modelo de dados

| Tabela | Campos | Observações |
|---|---|---|
| `users` | id, username, password_hash, created_at | Um usuário admin na v1 |
| `tracks` | hash (PK), kind, title, artist, duration_ms, size_bytes, loudness_lufs, original_name, created_at, deleted_at | `kind`: `music` ou `offer`. `deleted_at` = exclusão lógica |
| `playlists` | id, name, shuffle (bool), created_at, updated_at | |
| `playlist_items` | id (UUID estável), playlist_id, position, track_hash | Único (playlist_id, position). O `id` é preservado ao reordenar; âncoras de ofertas apontam para ele |
| `stores` | id, slug, name, volume (0–100), bt_device_name, timezone, min_offer_gap_s, crossfade_s, token_hash, created_at | `timezone` padrão `America/Sao_Paulo`; `min_offer_gap_s` padrão 300; `crossfade_s` padrão 5 (0 = sem mixagem) |
| `schedules` | id, store_id, playlist_id, days_mask, start_time, end_time | `days_mask`: bit 0 = domingo … bit 6 = sábado |
| `store_manifests` | store_id, version, content_hash, json, created_at | Mantém as últimas 20 versões |
| `store_state` | store_id, applied_version, pending_version, sync_progress, now_playing_hash, position_ms, player_state, bt_state, volume, disk_free_mb, agent_version, last_seen_at | Atualizado pela telemetria |
| `enroll_codes` | code_hash, store_id, expires_at, used_at | Código de vinculação de uma loja |
| `commands` | id, store_id, type, args_json, created_at, sent_at, result, error | Histórico de comandos |
| `events` | id, store_id, type, data_json, created_at | Eventos do agente (90 dias) |
| `play_history` | id, store_id, track_hash, started_at | Histórico do que tocou (90 dias) |
| `offers` | id, name, audio_hash, valid_from, valid_until, duck_level (0–100, padrão 30), enabled, created_at, updated_at | `audio_hash` aponta para `tracks` com `kind = offer` |
| `offer_stores` | offer_id, store_id | Sem linhas = a oferta vale para todas as lojas |
| `offer_placements` | id, offer_id, kind, mode, playlist_id, playlist_item_id, offset_ms, every_n, every_minutes, clock_time, days_mask, playlist_ids_json | Campos usados conforme o `kind` (seção 10.3.4) |
| `offer_plays` | id, store_id, offer_id, placement_id, started_at, result, reason | `result`: `played`, `interrupted`, `skipped` (90 dias) |
| `agent_releases` | version, sha256, signature, file_name, created_at, is_current | |
| `audit_log` | id, user_id, action, target, data_json, created_at | Tudo que o admin alterou |
| `settings` | key, value | Telegram, limites, etc. |

### 6.4 Geração do manifesto

- O manifesto de uma loja é gerado a partir de: configurações da loja + agendas da loja + playlists referenciadas pelas agendas + metadados das faixas dessas playlists + ofertas da loja (vigentes ou que entram em vigor nos próximos 7 dias) e seus posicionamentos.
- Os manifestos de todas as lojas DEVEM ser regenerados todos os dias às 00:05, para incluir ofertas que entram na janela de 7 dias e retirar as vencidas.
- Toda alteração em qualquer um desses itens DEVE disparar a regeneração do manifesto de cada loja afetada.
- A versão SÓ é incrementada se o `content_hash` (SHA-256 do JSON canônico, sem o campo `version`) mudar.
- A versão é um inteiro monotônico por loja. NUNCA diminui.

### 6.5 Pipeline de ingestão de músicas

Executado em fila (uma música por vez, em segundo plano) para cada arquivo enviado:

1. **Validação:** extensão permitida (`mp3, m4a, aac, flac, wav, ogg, opus, wma`), tamanho ≤ 100 MB, `ffprobe` confirma que é áudio válido e com duração entre 30 s e 15 min. Falha → rejeita com motivo.
2. **Metadados:** título e artista das tags. Sem tags, usa o nome do arquivo no formato `Artista - Título`.
3. **Corte de silêncio** no início e no fim (`silenceremove`, limiar −50 dB), para transições curtas entre faixas.
4. **Fade:** fade-in de 0,5 s no início e fade-out de 3 s no fim (`afade`), para que músicas cortadas no fim terminem suavemente. Os tempos ficam em `settings` e valem para novas ingestões.
5. **Normalização de volume** em duas passadas (`loudnorm`): alvo **−16 LUFS integrado, pico real −1,5 dBTP**. Todas as músicas DEVEM sair com o mesmo volume percebido.
6. **Codificação:** AAC-LC 160 kbps, 44,1 kHz, estéreo, `.m4a`, com `+faststart`.
7. **Hash:** SHA-256 do arquivo final. Se o hash já existe na biblioteca, a música é considerada duplicata e não é armazenada de novo.
8. **Armazenamento:** move para `library\<2 chars>\<hash>.m4a` e guarda o original em `originals\`.
9. **Registro** na tabela `tracks` e notificação ao painel via WebSocket.

**Perfil de ofertas.** Áudios enviados pela tela de Ofertas passam pelo mesmo pipeline, com estas diferenças:

| Parâmetro | Música | Oferta |
|---|---|---|
| Duração aceita | 30 s a 15 min | 3 s a 120 s |
| Alvo de volume | −16 LUFS | **−14 LUFS** (a voz DEVE se destacar sobre a música abaixada) |
| Pico real | −1,5 dBTP | −1,5 dBTP |
| `tracks.kind` | `music` | `offer` |
| Fade (passo 4) | 0,5 s / 3 s | Não (a locução fica intacta) |
| Aparece na Biblioteca | Sim | Não (só na tela de Ofertas) |

Os binários `ffmpeg.exe` e `ffprobe.exe` ficam em `app\tools\` e são baixados pelo script de instalação, nunca commitados.

### 6.6 Alertas (Telegram)

> **Adiado pela D21:** a v1 inicial não envia alertas pelo Telegram. As condições abaixo aparecem no dashboard do painel.

O Hub DEVE enviar alerta quando, **dentro do horário de funcionamento da loja** (janela de alguma agenda ativa):

| Condição | Tempo |
|---|---|
| Loja sem heartbeat | > 10 min |
| Bluetooth desconectado | > 5 min |
| Sincronização sem progresso | > 60 min |
| Disco livre da loja | < 1 GB |
| Agente reiniciou mais de 3 vezes | em 1 h |

Cada alerta é enviado uma única vez por ocorrência e gera uma mensagem de "resolvido" quando a condição termina.

### 6.7 Backup

- Diário às 02:00: `VACUUM INTO` do SQLite para `backups\marumbi-AAAA-MM-DD.db`, mantendo 30 dias.
- A pasta `data\` (banco, biblioteca e originais) DEVE ser copiada para fora do PC do escritório. Opção gratuita: pasta sincronizada do Google Drive para desktop (15 GB gratuitos; ≈ 1.500 músicas processadas + originais cabem com folga até algumas centenas de músicas, depois avaliar).

---

## 7. Painel administrativo (frontend)

### 7.1 Regras de UX

- ~~**Mobile first.** Toda tela DEVE funcionar em 360 px de largura. O celular é o controle remoto principal.~~ **Desktop first (D21):** as telas são pensadas para o computador, onde o administrador edita; no celular (360 px), o dashboard e o controle ao vivo das lojas continuam utilizáveis.
- **Salvar = publicar.** Não existe rascunho na v1. Ao salvar uma playlist, ela vai para as lojas.
- Toda ação destrutiva (remover música da biblioteca, remover loja) pede confirmação.
- Toda tela mostra, de forma visível, se cada loja afetada já aplicou a alteração.
- Atualização em tempo real via WebSocket. O painel NUNCA exige recarregar a página para ver o estado.
- Interface em português do Brasil.

### 7.2 Telas

**Login**
- Usuário e senha. Sessão de 30 dias por dispositivo.

**Dashboard**
- Um cartão por loja com: nome; status online/offline; tocando agora (título, artista, barra de progresso); Bluetooth 🟢/🔴; versão aplicada × versão atual ("Sincronizado ✅" ou "Baixando 3/10"); volume; último sinal.
- Botões rápidos no cartão: ⏯ tocar/pausar, ⏭ pular, volume −/+.
- Próxima oferta prevista e última oferta tocada.

**Biblioteca**
- Upload múltiplo por arrastar ou selecionar, com barra de progresso por arquivo e status de processamento (na fila / processando / pronto / erro com motivo).
- Lista com busca por título/artista, duração e em quais playlists a música está.
- Pré-escuta no navegador.
- Edição de título/artista.
- Remover da biblioteca (bloqueado se a música estiver em alguma playlist; o painel lista quais).

**Playlists**
- Lista de playlists e quais lojas/horários as usam.
- Editor em **duas colunas**:
  - **Coluna de músicas (esquerda):** lista ordenada com arrastar e soltar (funciona no toque), tempo acumulado ao lado de cada música, adicionar músicas da biblioteca (busca), remover, alternar "aleatório", duração total.
  - **Coluna de ofertas (direita):** alinhada à coluna de músicas. ~~Uma oferta arrastada para o **espaço entre duas músicas** cria uma âncora `anchorBetween`.~~ (fora do painel pela D24) Uma oferta arrastada para **cima de uma música** cria uma âncora `anchorDuring`, com um marcador de tempo ajustável ("aos 1:20 desta música").
  - Com "aleatório" ativado, a coluna de ofertas fica bloqueada (âncoras só existem em playlist sequencial).
  - Ao remover uma música que tem âncora, o painel DEVE avisar antes de salvar.

**Ofertas**
- Lista de ofertas com status calculado: agendada, no ar, vencida, desativada.
- Criar/editar oferta: nome (ex.: "Picanha R$ 69,90"), upload do áudio, pré-escuta, validade (início e fim, com data e hora), lojas (todas ou selecionadas), nível do ducking (padrão 30%), ativar/desativar.
- Regras de recorrência da oferta: ~~"a cada N músicas",~~ "a cada X minutos" ~~(entre ou durante)~~, "em horário fixo" (dias da semana + hora ~~; entre ou durante~~), com restrição opcional a playlists específicas. Pela D24 toda oferta toca durante a música, com ducking.
- Lista das âncoras dessa oferta em cada playlist (somente leitura; edição no editor de playlist).
- Contagem de execuções por loja (tocou, interrompida, descartada) nos últimos 7 dias.
- Aviso visível em toda oferta que vence nas próximas 24 h.

**Loja (detalhe)**
- Controle ao vivo: tocar/pausar, pular, volume (slider), "tocar agora" (escolhe uma música do manifesto ativo), "tocar oferta agora" (escolhe uma oferta vigente da loja), "reconectar Bluetooth".
- Intervalo mínimo entre ofertas da loja (padrão 5 min).
- Agenda: grade semanal com faixas de horário → playlist. Validação: faixas não podem se sobrepor no mesmo dia.
- Fila atual (próximas 10 faixas).
- Histórico do que tocou, músicas e ofertas (hoje / 7 dias).
- Eventos e logs recentes do agente.
- Configurações da loja: nome, volume padrão, tempo de mixagem entre músicas (padrão 5 s; 0 desativa).
- Manutenção: gerar código de vinculação, reiniciar agente, atualizar agente.

**Configurações**
- Trocar senha, ~~bot do Telegram (token e chat ID, com botão "enviar teste")~~ (adiado pela D21), versão atual do agente para distribuição, status de backup.

### 7.3 Estrutura do frontend

```
web/panel/src/
  api/          cliente HTTP tipado + cliente WebSocket
  components/   componentes reutilizáveis
  features/
    auth/ dashboard/ library/ playlists/ offers/ stores/ settings/
  hooks/
  lib/          formatadores (duração, datas)
  types/        tipos gerados a partir dos contratos do backend
  App.tsx  main.tsx  router.tsx
```

- O build (`vite build`) gera arquivos estáticos que o Hub serve em `/`.
- Os tipos TypeScript dos DTOs DEVEM ser gerados a partir do OpenAPI do Hub (`openapi-typescript`), nunca escritos à mão.

---

## 8. Agente da loja (Windows)

### 8.1 Instalação no sistema

| Item | Valor |
|---|---|
| Nome do serviço | `MarumbiSom.Agent` |
| Conta | LocalSystem |
| Início | Automático (Início Atrasado) |
| Recuperação | Reiniciar após 10 s / 10 s / 60 s; zerar contagem após 1 dia |
| Binários | `C:\Program Files\MarumbiSom\` |
| Dados | `C:\ProgramData\MarumbiSom\` (ACL: somente SYSTEM e Administradores) |
| Porta de rede | Nenhuma de entrada. Somente `127.0.0.1:47800` para a configuração local |

```
C:\ProgramData\MarumbiSom\
  config.json          configuração (token protegido com DPAPI, escopo máquina)
  manifests\active.json
  manifests\pending.json
  cache\ab\abcdef…1234.m4a
  cache\.part\         downloads incompletos
  state.json           posição de reprodução por playlist, último volume
  logs\
```

### 8.2 Módulos

| Módulo | Responsabilidade |
|---|---|
| `ConfigStore` | Lê/grava `config.json`; protege o token com DPAPI (`DataProtectionScope.LocalMachine`) |
| `HubClient` | Mantém o WebSocket com o Hub; reconexão com backoff exponencial 1 s → 60 s com variação aleatória; chamadas HTTP com token |
| `ManifestStore` | Guarda `active.json` e `pending.json` com escrita atômica; rejeita versões menores que a ativa |
| `SyncEngine` | Diferença manifesto × cache, fila de download (1 por vez), retomada via HTTP Range, verificação SHA-256, promoção do pendente a ativo |
| `CacheManager` | Remove arquivos não referenciados há mais de 7 dias; limite total de 10 GB; NUNCA remove arquivo referenciado |
| `Scheduler` | A cada 15 s determina a playlist ativa conforme agenda, dia da semana e fuso horário |
| `PlayerController` | Gerencia os três processos mpv (dois de música, alternados na mixagem, e um de ofertas), a fila, o estado, a mixagem e as rampas de volume; watchdog de todos |
| `SpotScheduler` | Avalia âncoras e regras de ofertas, aplica prioridade, intervalo mínimo e validade, e dispara as ofertas (seção 10.3) |
| `BluetoothGuardian` | Mantém o rádio ligado e a caixa conectada (seção 9) |
| `AudioEndpointManager` | Localiza o endpoint de áudio da caixa, impõe volume, fixa a saída padrão do Windows |
| `Telemetry` | Envia status a cada 30 s e imediatamente em mudanças relevantes |
| `LocalConfigServer` | Página de configuração local protegida por senha em `127.0.0.1:47800` |
| `UpdateManager` | Baixa, verifica assinatura e aplica atualizações do agente |

### 8.3 Sincronização (SyncEngine)

1. Recebe `manifest.changed` ou detecta versão nova no polling de 60 s.
2. Baixa o manifesto (`GET /agent/manifest`). Se a versão for ≤ à ativa, ignora.
3. Grava como `pending.json`.
4. Lista os hashes do pendente que não estão no cache.
5. Para cada um: baixa para `cache\.part\<hash>.part` com retomada (Range), respeitando o **limite de banda configurável (padrão 2 Mbps)**.
6. Calcula o SHA-256. Diferente do esperado → apaga e tenta de novo (até 3 vezes; depois registra erro e tenta novamente em 10 min).
7. Move para `cache\<2 chars>\<hash>.m4a`.
8. Quando todos os arquivos do pendente estão presentes e verificados: substitui `active.json` pelo pendente (atomicamente), avisa o `PlayerController` e envia `manifest.applied`.
9. Se chegar uma versão ainda mais nova durante o download, o pendente é substituído e o download continua aproveitando o que já foi baixado.

Na inicialização, o agente DEVE verificar a existência de todos os arquivos do `active.json`. Arquivo ausente ou corrompido é baixado de novo; enquanto isso, a faixa é pulada.

### 8.4 Configuração local (LocalConfigServer)

- Página web em `http://127.0.0.1:47800`, acessível somente na própria máquina.
- Atalho "Configurar Som" na área de trabalho dos administradores (não na do funcionário).
- Protegida por senha local (hash PBKDF2). 5 tentativas erradas → bloqueio de 15 min.
- Funções permitidas:
  - vincular a loja ao Hub (endereço + código de vinculação);
  - escolher a caixa Bluetooth entre os dispositivos já pareados no Windows;
  - tocar som de teste;
  - ver status, versão e últimos logs;
  - trocar a senha local.
- O pareamento da caixa é feito pelas Configurações do Windows (seção 14), não pelo agente.

### 8.5 Vinculação de uma loja (enrollment)

1. No painel: Loja → "Gerar código de vinculação". O Hub gera um código de 8 caracteres válido por 30 min.
2. No PC da loja: a configuração local recebe o endereço do Hub e o código.
3. O agente chama `POST /agent/enroll` com o código. O Hub devolve o `store_id` e um **token permanente** (32 bytes aleatórios), e guarda apenas o hash do token.
4. O agente grava o token protegido por DPAPI.
5. O código é invalidado após o uso.

### 8.6 Proteção do PC compartilhado

- O funcionário DEVE usar uma conta Windows **padrão** (sem administrador). Sem isso, a regra 11 é impossível de cumprir.
- O agente não tem janela nem ícone na bandeja na v1.
- Consumo-alvo: < 200 MB de RAM e < 2% de CPU médio (agente + três mpv).

---

## 9. Guardião Bluetooth

### 9.1 Máquina de estados

```
            ┌─────────────┐
   boot ───►│ Inicializando│── serviços bthserv + Audiosrv ativos ──┐
            └─────────────┘                                          ▼
                                                            ┌───────────────┐
           ┌────────────────────────────────────────────────│ VerificarRádio│
           │                                                └──────┬────────┘
           │ rádio ligado                                          │ rádio desligado
           ▼                                                       ▼
   ┌───────────────┐   endpoint ativo   ┌────────────┐      ┌──────────────┐
   │ Conectando    │──────────────────► │ Conectado  │      │ LigandoRádio │
   └──────┬────────┘                    └─────┬──────┘      └──────┬───────┘
          │ falhou                            │ endpoint sumiu     │ (níveis 1→2→3)
          ▼                                   ▼                    │
   ┌───────────────┐                  volta para Conectando ◄──────┘
   │ Aguardando    │── backoff 5 s, 10 s, 20 s, 40 s, 60 s (máx) ──► Conectando
   └───────────────┘
```

### 9.2 Ligar o Bluetooth (escalonamento)

O guardião aplica o nível mais leve primeiro e só sobe de nível se falhar:

| Nível | Situação | Ação |
|---|---|---|
| 1 | Rádio desligado por software (botão/modo avião) | `Windows.Devices.Radios.Radio.SetStateAsync(RadioState.On)` |
| 2 | Serviço de Bluetooth parado ou travado | Reiniciar o serviço `bthserv` |
| 3 | Adaptador desativado ou travado | Desativar e reativar o dispositivo do adaptador via PnP (`pnputil /disable-device` + `/enable-device`, ou SetupAPI) |

O nível 3 só pode ser executado no máximo 1 vez a cada 10 minutos.

### 9.3 Conectar a caixa (escalonamento)

| Método | Descrição |
|---|---|
| A | Solicitar reconexão no endpoint de áudio Bluetooth via `IKsControl` com `KSPROPSETID_BtAudio` / `KSPROPERTY_ONESHOT_RECONNECT` |
| B | Desligar e religar o serviço A2DP Sink da caixa pareada via `BluetoothSetServiceState` (UUID `0000110B-0000-1000-8000-00805F9B34FB`) |
| C | Executar nível 3 da tabela 9.2 e voltar ao método A |

### 9.4 Identificação da caixa

- A configuração guarda o **endereço Bluetooth (MAC)** e o nome da caixa escolhida.
- O endpoint de áudio correspondente é localizado pelo `ContainerId` do dispositivo Bluetooth com aquele endereço. NUNCA localizar apenas pelo nome amigável (pode mudar ou repetir).

### 9.5 Comportamento de áudio

- Os três processos mpv (dois de música e um de ofertas) são iniciados com `--audio-device=wasapi/{id-do-endpoint-da-caixa}`. O áudio vai **somente** para a caixa, e o Windows mistura os fluxos.
- Quando a caixa conecta, o Windows tende a torná-la a saída padrão (e os sons do sistema do caixa passam a sair na caixa da loja). O `AudioEndpointManager` DEVE restaurar a saída padrão anterior (via interface `IPolicyConfig`, não documentada). Se a interface falhar em alguma versão do Windows, registrar aviso e seguir sem essa função. Destino válido, viva-voz da caixa e desistência após três trocas seguidas: D25.
- Volume do endpoint da caixa fixado em 100%. O volume da loja é controlado pelo volume do mpv (0–100, definido no painel). O agente reaplica ambos a cada 10 s, desfazendo alterações feitas por funcionários. Durante uma oferta, o valor imposto à música é o do ducking em andamento.
- Sem caixa conectada: o player pausa e guarda a posição. Ao reconectar, retoma da mesma posição.

### 9.6 Metas

| Situação | Meta |
|---|---|
| Ligar o PC até começar a tocar (dentro do horário) | ≤ 120 s após o Windows iniciar |
| Caixa desligada e religada | Reconectada em ≤ 30 s |
| Rádio desligado manualmente | Religado em ≤ 15 s |
| Desconexão pelo menu do Windows | Reconectada em ≤ 30 s |

---

## 10. Player, reprodução e ofertas

### 10.1 mpv

Parâmetros de inicialização:

```
mpv --idle=yes --no-video --no-terminal --gapless-audio=yes
    --input-ipc-server=\\.\pipe\marumbisom-mpv-a
    --audio-device=wasapi/{endpoint}
    --volume=<volume da loja> --volume-max=100
```

- Há dois processos de música, A e B (`marumbisom-mpv-a` e `marumbisom-mpv-b`), que se alternam: enquanto um toca, o outro espera a próxima faixa para a mixagem (seção 10.2.1).
- Com a mixagem desativada (`crossfadeS = 0`), só o processo A é usado: o `PlayerController` envia uma faixa por vez (`loadfile`) e deixa a próxima pré-carregada (`loadfile … append`) para transição sem pausa.
- Watchdog: se o processo mpv morrer, reinicia em 2 s. Se estiver "tocando" sem a posição avançar por 30 s, mata e reinicia.
- Um terceiro processo mpv, exclusivo para ofertas, usa os mesmos parâmetros, com `--input-ipc-server=\\.\pipe\marumbisom-mpv-spot`. Ele fica ocioso fora das ofertas e tem o mesmo watchdog.

### 10.2 Regras de reprodução

1. **Playlist ativa** = a da agenda cuja janela contém o horário atual. Fora de todas as janelas = silêncio.
2. **Troca de janela de agenda:** a nova playlist começa ao terminar a faixa atual.
3. **Fim do horário:** fade-out de 3 s e parada no horário exato.
4. **Ordem:** sequencial conforme a posição. Com `shuffle = true`, embaralha uma vez por ciclo completo, sem repetir faixa até passar por todas.
5. **Continuidade:** o agente guarda em `state.json` a última posição de cada playlist. Após reiniciar o PC, continua da próxima faixa, nunca volta para a primeira.
6. **Mudança de manifesto:** a faixa atual termina; a fila é reconstruída a partir da faixa seguinte à atual na nova ordem. Se a faixa atual foi removida, a fila recomeça do ponto equivalente da nova lista (mesma posição numérica, limitada ao tamanho da lista).
7. **"Tocar agora":** interrompe a atual, toca a escolhida e depois segue a fila normal.
8. **"Pausar" pelo admin:** vale até "tocar" ou até a próxima janela de agenda começar.
9. Toda faixa iniciada é registrada (`track.started`) para o histórico.

### 10.2.1 Mixagem entre músicas (crossfade)

1. A `crossfadeS` segundos do fim da faixa atual (padrão 5, configurável por loja), a próxima faixa começa no processo de música ocioso e as duas trocam de volume ao mesmo tempo, em rampa de potência constante (*equal-power*), durante `crossfadeS`.
2. O fim da faixa é o fim real do arquivo, que já vem sem silêncio e com fade-out (seção 6.5).
3. Faixa mais curta que 3 × `crossfadeS`: a mixagem usa um terço da duração da faixa.
4. Não há mixagem: (a) antes de oferta `between` ou âncora `anchorBetween` (a música termina, a oferta toca, a próxima começa 300 ms depois, seção 10.3.2); (b) no fim do horário (fade-out de 3 s, item 3 da seção 10.2); (c) com `crossfadeS = 0` (emenda sem pausa, seção 10.1).
5. "Pular" e "tocar agora" fazem a transição em 1 s, nunca em degrau.
6. Troca de janela de agenda: a primeira faixa da nova playlist entra com mixagem.
7. Volume de cada processo de música = volume da loja × ganho da mixagem × ganho do ducking (quando há oferta `during`). Uma oferta durante a mixagem abaixa os dois processos.
8. `track.started` é enviado quando a próxima faixa começa (início da mixagem); a posição salva em `state.json` passa a ser a da nova faixa nesse momento.
9. Se um dos processos de música morrer durante a mixagem, a faixa que entrava continua no outro, com o volume levado ao alvo em rampa de 1 s.

### 10.3 Ofertas (spots) e ducking

#### 10.3.1 Conceito

Oferta é um áudio curto de locução (ex.: "Picanha R$ 69,90 até sábado"). Ela toca no segundo processo mpv, independente do player de música, no mesmo endpoint da caixa. O Windows mistura os dois fluxos; o agente controla o volume de cada um.

#### 10.3.2 Modos de entrada

| Modo | Comportamento |
|---|---|
| Entre músicas (`between`) | A música atual termina; a oferta toca em volume cheio; a próxima música começa 300 ms após o fim da oferta. O painel não oferece este modo (D24) |
| Durante a música (`during`) | Ducking, conforme 10.3.3 |

#### 10.3.3 Ducking

1. **T0:** rampa do volume da música de 100% até `duckLevel` (padrão 30%) em 800 ms.
2. **T0 + 800 ms:** a oferta começa, em volume cheio.
3. **Fim da oferta:** rampa da música de volta a 100% em 1.500 ms.
4. Se a música terminar durante a oferta, a próxima música começa já em `duckLevel` e só sobe quando a oferta terminar.
5. Rampas são feitas em passos de no máximo 40 ms. NUNCA alterar o volume em degrau.
6. `duckLevel` é a fração da amplitude da música em relação ao volume da loja (30% ≈ −10,5 dB). Como o volume do mpv é cúbico (ganho = (volume/100)³), o volume aplicado ao mpv durante a oferta é volume da loja × ∛(duckLevel/100). Exemplo: loja em 70 e ducking de 30% → mpv da música em 46,9 durante a oferta (decisão D17).
7. O volume da oferta é o volume da loja. O destaque da voz vem da normalização mais alta da locução (−14 LUFS, seção 6.5).

#### 10.3.4 Posicionamento

| `kind` | Campos | Comportamento |
|---|---|---|
| `anchorBetween` | `playlistId`, `afterItemId` | Toca quando o item indicado termina |
| `anchorDuring` | `playlistId`, `itemId`, `offsetMs` | Toca durante o item indicado, no tempo indicado |
| `everyTracks` | `n` | Toca entre músicas a cada `n` músicas tocadas (sempre `between`) |
| `everyMinutes` | `minutes`, `mode` | Toca a cada X minutos, contados desde o último disparo desta regra |
| `clock` | `time`, `days`, `mode` | Toca no horário indicado, nos dias indicados |

Regras de posicionamento:

- Âncoras são presas ao **item** da playlist (`playlist_items.id`), NUNCA à posição numérica. Reordenar a playlist move a oferta junto com a música.
- Remover um item que tem âncora remove a âncora. O painel DEVE avisar antes de salvar.
- Âncoras SÓ existem em playlists com `shuffle = false`. Ativar o aleatório numa playlist com âncoras exige confirmação e remove as âncoras.
- `offsetMs` DEVE ser ≤ duração da música − 5 s. O painel valida; o agente limita ao máximo permitido se receber valor fora disso.
- Regras (`everyTracks`, `everyMinutes`, `clock`) valem para todas as playlists da loja, exceto quando restringidas por `playlistIds`.
- `clock` com `mode = between` toca no fim da música em andamento no horário indicado.
- Os contadores das regras (`everyTracks`, `everyMinutes`) são persistidos em `state.json` e sobrevivem a reinicializações.

#### 10.3.5 Prioridade e conflitos

- Prioridade, da maior para a menor: comando "tocar oferta agora" > `clock` > âncoras > `everyMinutes` > `everyTracks`.
- Intervalo mínimo entre ofertas por loja: `minOfferGapS` (padrão 300 s).
- Regras respeitam o intervalo mínimo. Âncoras e o comando "tocar oferta agora" ignoram o intervalo (foram posicionados deliberadamente pelo admin), mas NUNCA se sobrepõem a outra oferta.
- Oferta disparada enquanto outra toca, ou durante o intervalo mínimo, entra numa fila de no máximo 3 ofertas, ordenada pela prioridade.
- Oferta na fila há mais de 10 min é descartada e registrada como `skipped` com motivo `stale`.
- A mesma oferta NUNCA aparece duas vezes na fila.

#### 10.3.6 Validade e escopo

- Toda oferta tem `validFrom` e `validUntil` (data e hora no fuso da loja).
- O Hub inclui no manifesto apenas ofertas ativas (`enabled = true`), vigentes ou que entram em vigor nos próximos 7 dias (seção 6.4).
- O agente confere a validade com o relógio local imediatamente antes de cada disparo. Fora da validade = não toca, mesmo estando no manifesto (regra 14).
- Oferta sem lojas selecionadas vale para todas as lojas.

#### 10.3.7 Situações especiais

| Situação | Comportamento |
|---|---|
| Fora das janelas da agenda | Ofertas não tocam (regra 15) |
| Música pausada pelo admin | Ofertas não tocam; "tocar oferta agora" toca mesmo assim |
| Caixa desconectada no momento do disparo | Oferta entra na fila e segue a regra de descarte de 10 min |
| Caixa cai durante a oferta | Oferta interrompida, registrada como `interrupted`; NÃO é repetida ao reconectar |
| Admin pula a música durante um ducking | A próxima música começa em `duckLevel` e sobe quando a oferta terminar |
| Áudio da oferta ausente ou corrompido no cache | Oferta registrada como `skipped` com motivo `missing_file`; o SyncEngine baixa de novo |

#### 10.3.8 Registro

Todo disparo gera `spot.started`; o fim gera `spot.finished` com resultado `played` ou `interrupted`; todo descarte gera `spot.skipped` com o motivo. O Hub grava tudo em `offer_plays`.

---

## 11. Protocolos

Todos os contratos (DTOs) ficam em `MarumbiSom.Contracts`, compartilhados entre Hub e agente. JSON em `camelCase`. Datas em ISO 8601 com fuso.

### 11.1 Manifesto

```json
{
  "storeId": "loja-2",
  "version": 47,
  "contentHash": "sha256:…",
  "generatedAt": "2026-10-22T16:20:00-03:00",
  "timezone": "America/Sao_Paulo",
  "volume": 70,
  "crossfadeS": 5,
  "schedules": [
    { "days": [1,2,3,4,5,6], "start": "08:00", "end": "12:00", "playlistId": "manha" },
    { "days": [1,2,3,4,5,6], "start": "12:00", "end": "20:00", "playlistId": "tarde" }
  ],
  "playlists": {
    "manha": { "shuffle": true,  "items": [{ "id": "it-10", "hash": "a3f9…" }, { "id": "it-11", "hash": "77b2…" }] },
    "tarde": { "shuffle": false, "items": [{ "id": "it-88", "hash": "9d01…" }, { "id": "it-90", "hash": "a3f9…" }] }
  },
  "tracks": {
    "a3f9…": { "kind": "music", "size": 4213377, "durationMs": 211000, "title": "…", "artist": "…" },
    "c41e…": { "kind": "offer", "size": 310442, "durationMs": 14000, "title": "Picanha R$ 69,90" }
  },
  "spots": {
    "minOfferGapS": 300,
    "offers": {
      "of-12": { "hash": "c41e…", "name": "Picanha R$ 69,90", "duckLevel": 30,
                 "validFrom": "2026-10-23T07:00:00-03:00", "validUntil": "2026-10-25T20:00:00-03:00" }
    },
    "placements": [
      { "id": "pl-1", "offerId": "of-12", "kind": "anchorBetween", "playlistId": "tarde", "afterItemId": "it-88" },
      { "id": "pl-2", "offerId": "of-12", "kind": "anchorDuring",  "playlistId": "tarde", "itemId": "it-90", "offsetMs": 80000 },
      { "id": "pl-3", "offerId": "of-12", "kind": "everyMinutes",  "minutes": 15, "mode": "during" },
      { "id": "pl-4", "offerId": "of-12", "kind": "clock", "time": "12:00", "days": [1,2,3,4,5,6], "mode": "during" }
    ]
  }
}
```

`days` usa 0 = domingo … 6 = sábado.

`tracks` contém músicas e áudios de ofertas; o SyncEngine sincroniza os dois do mesmo jeito. Âncoras só aparecem em playlists com `shuffle = false`.

### 11.2 API do painel (sessão por cookie)

| Método | Rota | Função |
|---|---|---|
| POST | `/api/auth/login` | Login |
| POST | `/api/auth/logout` | Logout |
| GET | `/api/tracks?q=` | Listar/buscar biblioteca |
| POST | `/api/tracks` | Upload (multipart, vários arquivos) |
| PATCH | `/api/tracks/{hash}` | Editar título/artista |
| DELETE | `/api/tracks/{hash}` | Remover (bloqueado se em uso) |
| GET | `/api/tracks/{hash}/audio` | Pré-escuta (suporta Range) |
| GET/POST | `/api/playlists` | Listar/criar |
| GET/PATCH/DELETE | `/api/playlists/{id}` | Detalhe/renomear/shuffle/remover |
| PUT | `/api/playlists/{id}/items` | Substituir a lista ordenada inteira (idempotente; itens existentes mantêm o `id`, itens novos vêm sem `id`) |
| PUT | `/api/playlists/{id}/anchors` | Substituir todas as âncoras de ofertas da playlist |
| GET/POST | `/api/stores` | Listar/criar loja |
| GET/PATCH | `/api/stores/{id}` | Detalhe/configurações |
| PUT | `/api/stores/{id}/schedules` | Substituir a agenda inteira |
| POST | `/api/stores/{id}/enroll-code` | Gerar código de vinculação |
| POST | `/api/stores/{id}/commands` | Comando ao vivo |
| GET | `/api/stores/{id}/history` | Histórico de reprodução |
| GET | `/api/stores/{id}/events` | Eventos |
| GET | `/api/stores/{id}/offers` | Ofertas vigentes da loja, para "tocar oferta agora" (D26 g) |
| GET/POST | `/api/offers` | Listar/criar oferta (~~criação em multipart, com o áudio~~ JSON com o `audioHash` do áudio já enviado, D26 a) |
| GET/PATCH/DELETE | `/api/offers/{id}` | Detalhe/editar/remover oferta |
| PUT | `/api/offers/{id}/rules` | Substituir as regras de recorrência da oferta |
| GET | `/api/offers/{id}/audio` | Pré-escuta da oferta |
| GET | `/api/offers/{id}/plays` | Execuções por loja |
| GET/PUT | `/api/settings` | Configurações gerais |
| POST | `/api/releases` | Publicar versão assinada do agente |
| WS | `/ws/panel` | Atualizações em tempo real para o painel |

Tipos de comando: `play`, `pause`, `next`, `setVolume {volume}`, `playNow {hash}`, `playOffer {offerId}`, `reconnectBluetooth`, `restartAgent`, `updateAgent`.

### 11.3 API do agente (token Bearer)

| Método | Rota | Função |
|---|---|---|
| POST | `/agent/enroll` | Troca código por token (sem token) |
| GET | `/agent/manifest` | Manifesto atual; `If-None-Match` com a versão → `304` |
| GET | `/agent/files/{hash}` | Download de música (suporta Range) |
| GET | `/agent/releases/current` | Metadados da versão atual do agente |
| GET | `/agent/releases/{version}/package` | Pacote de atualização |
| WS | `/agent/ws` | Canal bidirecional |

### 11.4 Mensagens WebSocket do agente

Hub → Agente:

```json
{ "type": "manifest.changed", "version": 48 }
{ "type": "command", "id": "c-123", "command": "setVolume", "args": { "volume": 60 } }
```

Agente → Hub:

```json
{ "type": "hello", "storeId": "loja-2", "agentVersion": "1.2.0", "activeVersion": 47 }
{ "type": "status", "player": "playing", "nowPlaying": "a3f9…", "positionMs": 52000,
  "volume": 70, "bluetooth": "connected", "pendingVersion": 48, "syncProgress": { "done": 3, "total": 10 },
  "diskFreeMb": 81234, "uptimeS": 7200 }
{ "type": "command.result", "id": "c-123", "ok": true }
{ "type": "manifest.applied", "version": 48 }
{ "type": "event", "event": "bluetooth.lost", "data": { "method": "A" } }
{ "type": "track.started", "hash": "a3f9…", "at": "2026-10-22T16:21:05-03:00" }
{ "type": "spot.started", "offerId": "of-12", "placementId": "pl-2", "mode": "during", "at": "2026-10-22T16:22:25-03:00" }
{ "type": "spot.finished", "offerId": "of-12", "result": "played" }
{ "type": "spot.skipped", "offerId": "of-12", "placementId": "pl-3", "reason": "stale" }
```

`status` é enviado a cada 30 s e imediatamente após qualquer mudança de player, Bluetooth ou sincronização. Desde a D23, o `status` também pode trazer `"playlistId": "p3"` (playlist da agenda em vigor) e `"queue": ["b71c…", …]` (próximas até 10 faixas). Desde a D26, pode trazer `"nextSpot": { "offerId": "of-12", "at": "2026-10-22T16:30:00-03:00" }` (próxima oferta prevista).

---

## 12. Segurança

### 12.1 Camadas

| Camada | Medida |
|---|---|
| Rede | Hub e lojas só se comunicam pelo Tailscale (WireGuard). Hub escuta somente em `127.0.0.1`. Lojas sem portas de entrada |
| Transporte | HTTPS fornecido pelo Tailscale Serve |
| Acesso entre máquinas | ACL da seção 5.2. Lojas não enxergam umas às outras |
| Painel | Usuário + senha (hash PBKDF2 do ASP.NET Core Identity `PasswordHasher`); cookie `HttpOnly`, `Secure`, `SameSite=Strict`; proteção CSRF; limite de 5 tentativas de login por minuto |
| Agente → Hub | Token por loja, 32 bytes aleatórios; Hub guarda apenas o hash; revogável no painel |
| Token no PC da loja | DPAPI escopo máquina + ACL da pasta restrita a SYSTEM/Administradores |
| Configuração local | Apenas `127.0.0.1`, senha com bloqueio após 5 erros |
| Arquivos de música | Identificados e verificados por SHA-256; upload validado com ffprobe; arquivos NUNCA executados |
| Atualização do agente | Pacote `.zip` assinado com Ed25519. Chave privada SOMENTE no PC de desenvolvimento do admin (nunca no Hub, nunca no Git). Chave pública embutida no agente |
| Auditoria | Toda alteração feita no painel vai para `audit_log` |
| Segredos | `appsettings.Production.json` e chaves fora do Git (`.gitignore`); exemplo versionado como `appsettings.example.json` |

### 12.2 Atualização remota do agente

1. Admin gera o pacote no PC de desenvolvimento e assina com a chave privada (script `scripts/sign-release.ps1`).
2. Admin publica no painel (`POST /api/releases`).
3. Admin aciona "Atualizar agente" em uma loja (piloto) e depois em todas.
4. Agente baixa, verifica SHA-256 e assinatura. Falha → descarta e reporta.
5. Agente inicia `MarumbiSom.Updater.exe` (processo separado), que para o serviço, faz backup da versão atual, substitui os binários e inicia o serviço.
6. Se o novo agente não enviar `hello` ao Hub em 2 minutos, o Updater restaura a versão anterior e inicia o serviço.

---

## 13. Estrutura do repositório e padrões de código

### 13.1 Estrutura

```
marumbi-som/
  README.md
  SISTEMA_SOM_MARUMBI.md            (este documento)
  .gitignore  .editorconfig  Directory.Build.props
  src/
    MarumbiSom.Contracts/           DTOs, manifesto, mensagens WS
    MarumbiSom.Hub/                 ASP.NET Core
    MarumbiSom.Agent/               Worker Service (serviço Windows)
    MarumbiSom.Agent.Windows/       Interop: Bluetooth, rádio, KS, Core Audio, PolicyConfig, DPAPI
    MarumbiSom.Updater/             Atualizador
  web/panel/                        React + Vite + TS
  tests/
    MarumbiSom.Hub.Tests/
    MarumbiSom.Agent.Tests/
    MarumbiSom.Contracts.Tests/
  scripts/
    install-hub.ps1
    install-agent.ps1               (usado pelo instalador)
    harden-store-pc.ps1
    download-tools.ps1              (baixa ffmpeg e mpv com verificação de hash)
    sign-release.ps1
    new-signing-key.ps1
    uninstall-agent.ps1
  installer/
    agent.iss                       Inno Setup
  infra/
    tailscale-acl.hujson
  docs/
    runbook.md                      procedimentos de operação
    checklist-instalacao-loja.md
```

### 13.2 Padrões

- .NET 10, `Nullable` habilitado, `TreatWarningsAsErrors` habilitado.
- Injeção de dependência e `IOptions<T>` para configuração.
- Toda interação com Windows (Bluetooth, áudio, rádio, serviços, PnP) DEVE estar atrás de interfaces em `MarumbiSom.Agent.Windows`, para que a lógica do agente seja testável com implementações falsas.
- Todo `HttpClient` via `IHttpClientFactory`, com timeout explícito.
- Todo loop de serviço respeita `CancellationToken` e tem tratamento de exceção com log, nunca derruba o processo.
- Logs estruturados (Serilog): arquivo rotativo diário, 14 dias, máx. 20 MB por arquivo.
- Commits pequenos e descritivos; uma funcionalidade por branch; `main` sempre compilando.
- Frontend: ESLint + Prettier; componentes funcionais; nenhum `any`.

---

## 14. Divisão de responsabilidades: humano × agente de IA

### 14.1 Somente o humano faz (exige presença, conta pessoal ou decisão)

**Contas e serviços**
- [ ] Criar a conta no Tailscale.
- [ ] Ativar MagicDNS e certificados HTTPS no console do Tailscale.
- [ ] Colar a política de ACL (escrita pelo agente de IA em `infra/tailscale-acl.hujson`) no console do Tailscale.
- [ ] Desativar a expiração de chave do Hub e confirmar nas lojas.
- [ ] Criar o repositório privado no GitHub.
- [ ] ~~Criar o bot do Telegram (BotFather), obter o token e o chat ID, e cadastrar no painel.~~ (adiado pela D21)
- [ ] Gerar a chave de assinatura (`scripts/new-signing-key.ps1`) e guardar a chave privada em local seguro, com cópia offline.

**PC do escritório (Hub)**
- [ ] Instalar o Tailscale e fazer login.
- [ ] Executar `install-hub.ps1` como administrador.
- [ ] Executar `tailscale serve --bg 5080`.
- [ ] Definir a senha do admin no primeiro acesso ao painel.
- [ ] Configurar o plano de energia para não suspender.
- [ ] Configurar a cópia da pasta `data\` para fora do PC (ex.: Google Drive para desktop).

**PC de cada loja**
- [ ] Garantir uma conta de administrador com senha forte, conhecida somente pelo admin.
- [ ] Garantir que o funcionário usa conta **padrão**, sem privilégios de administrador.
- [ ] Ligar o adaptador Bluetooth USB **direto em uma porta do PC** (não em hub USB), de preferência traseira, longe de metal e com a caixa a no máximo ~5 m sem paredes no caminho.
- [ ] Instalar o Tailscale, fazer login, ativar "Run unattended" e aplicar a tag `tag:loja`.
- [ ] Executar `harden-store-pc.ps1` como administrador e conferir o relatório que ele gera.
- [ ] Conferir no Gerenciador de Dispositivos que o adaptador Bluetooth e o hub raiz USB estão com "Permitir que o computador desligue este dispositivo para economizar energia" **desmarcado** (o script tenta; o humano confirma).
- [ ] (Opcional) Na BIOS: "Restore on AC Power Loss" = Power On, para o PC religar sozinho após queda de energia.
- [ ] Parear a caixa pelas Configurações do Windows (Bluetooth e dispositivos → Adicionar dispositivo).
- [ ] Executar o instalador do agente como administrador.
- [ ] Na configuração local: definir a senha local, informar o endereço do Hub e o código de vinculação gerado no painel, escolher a caixa, tocar o som de teste.
- [ ] Executar o checklist de aceite da loja (seção 16.3).

**Caixa de som**
- [ ] Desativar o desligamento automático, se a caixa permitir.
- [ ] Deixar a caixa sempre ligada na tomada.
- [ ] Ajustar o volume físico da caixa em um nível fixo e marcar ("não mexer").

**Conteúdo e decisões**
- [ ] Fornecer os arquivos de música.
- [ ] Fornecer os áudios das ofertas (locução pronta) e conferir preço e validade de cada oferta antes de publicar.
- [ ] Regularizar a execução pública (seção 20).
- [ ] Aprovar cada etapa conforme os critérios de aceite.
- [ ] Revisar e testar o código entregue antes de instalar nas lojas.

### 14.2 O agente de IA de programação faz

- [ ] Criar a estrutura do repositório (seção 13) e a solução .NET.
- [ ] Implementar `MarumbiSom.Contracts`.
- [ ] Implementar o Hub completo: API, WebSocket, banco e migrações, geração de manifesto, ingestão, alertas, backup, servir o painel.
- [ ] Implementar o painel completo (seção 7).
- [ ] Implementar o agente completo (seções 8, 9 e 10), incluindo toda a camada de interop com o Windows.
- [ ] Implementar o Updater.
- [ ] Escrever os scripts PowerShell: `install-hub`, `install-agent`, `harden-store-pc`, `download-tools`, `sign-release`, `new-signing-key`, `uninstall-agent`.
- [ ] Escrever o instalador Inno Setup do agente.
- [ ] Escrever `infra/tailscale-acl.hujson` conforme a seção 5.2.
- [ ] Escrever testes automatizados (seção 16.1) e o modo de simulação do agente (`--simulate`, com Bluetooth e player falsos).
- [ ] Escrever `docs/runbook.md` e `docs/checklist-instalacao-loja.md`.
- [ ] Manter este documento atualizado e registrar decisões na seção 18.

### 14.3 Conteúdo obrigatório do `harden-store-pc.ps1`

O script DEVE ser idempotente, exigir execução como administrador e gerar um relatório do que alterou:

- Desativar hibernação (o que desativa a Inicialização Rápida): `powercfg /hibernate off`.
- Suspensão e hibernação em "nunca" na energia AC.
- Desativar a suspensão seletiva de USB no plano ativo.
- Tentar desativar a economia de energia do adaptador Bluetooth e dos hubs USB (WMI `MSPower_DeviceEnable`).
- Definir o horário ativo do Windows Update cobrindo o horário de funcionamento (padrão 07:00–21:00).
- Garantir o fuso `E. South America Standard Time` e a sincronização de horário ativa (`w32tm /resync`).
- Garantir os serviços `bthserv` e `Audiosrv` com início automático.
- Listar as contas locais e alertar se a conta do funcionário for administradora.

---

## 15. Etapas de implementação e critérios de aceite

Cada etapa só começa quando a anterior foi aceita. Critério não atendido = etapa não concluída.

### Etapa 0 — Preparação (humano)
- [ ] Contas criadas (Tailscale, GitHub).
- [ ] Hub e um PC de loja piloto no Tailscale, conversando entre si.
- [ ] Caixa pareada no PC piloto.

### Etapa 1 — Prova de conceito: Bluetooth e áudio como serviço ⚠️ etapa de maior risco
Entrega: serviço mínimo que liga o rádio, conecta a caixa e toca um arquivo local em loop, executando como LocalSystem.

Aceite (no PC real da loja piloto):
- [ ] Após 5 reinicializações seguidas, a música começa em ≤ 120 s, **sem ninguém fazer login**.
- [ ] Caixa desligada e religada 5 vezes: reconecta em ≤ 30 s todas as vezes.
- [ ] Bluetooth desligado pelo menu do Windows: religado em ≤ 15 s.
- [ ] Caixa desconectada pelo menu do Windows: reconectada em ≤ 30 s.
- [ ] Adaptador desativado no Gerenciador de Dispositivos: reativado e reconectado.
- [ ] Sons do Windows continuam saindo no alto-falante do PC, não na caixa.
- [ ] Dois processos mpv tocando ao mesmo tempo na caixa por 30 min, sem falha nem atraso perceptível.
- [ ] Rampa 100% → 30% → 100% soa como transição suave, sem estalos.

**Plano B obrigatório se o áudio ou o rádio não funcionarem a partir da sessão do serviço (sessão 0):** dividir o agente em dois processos: o serviço (sincronização, Hub, agenda, vigia) e um processo oculto `MarumbiSom.AudioHost` executado na sessão do usuário logado (iniciado por tarefa agendada no logon), responsável por mpv, Bluetooth e endpoints, conversando com o serviço por named pipe. Nesse caso o PC da loja DEVE ter logon automático na conta padrão do funcionário. Registrar a decisão na seção 18.

### Etapa 2 — Agente offline completo
Entrega: player, agenda, cache, estado persistido, configuração local, lendo um manifesto de arquivo local (sem Hub).

Aceite:
- [ ] Agenda troca de playlist no horário certo (testado adiantando horários).
- [ ] Após reiniciar, continua da faixa seguinte, não da primeira.
- [ ] Matar o mpv pelo Gerenciador de Tarefas: volta a tocar em ≤ 5 s.
- [ ] Funcionário (conta padrão) não consegue parar o serviço.
- [ ] Transição entre músicas com mixagem de 5 s: sem silêncio, sem salto de volume, sem estalo.
- [ ] Com mixagem em 0, as faixas emendam sem pausa.
- [ ] Oferta entre músicas suspende a mixagem naquela transição.
- [ ] Testes automatizados do `Scheduler` e das regras da seção 10.2 passando.

### Etapa 3 — Hub backend + sincronização
Entrega: Hub com API, banco, ingestão, manifesto, WebSocket, enrollment; agente sincronizando.

Aceite:
- [ ] Upload de 20 músicas em formatos variados: todas processadas, com volume normalizado.
- [ ] Alterar a ordem via API: aplicado na loja em ≤ 60 s, sem cortar a faixa atual.
- [ ] Adicionar 10 músicas novas: baixadas, verificadas e só então aplicadas.
- [ ] Cabo de rede puxado no meio do download: retoma de onde parou.
- [ ] Arquivo do cache corrompido manualmente: detectado e baixado de novo.
- [ ] Hub desligado por 24 h: loja toca normalmente o tempo todo.
- [ ] Música enviada cortada no fim sai processada com fade-out de 3 s.

### Etapa 4 — Painel
Entrega: todas as telas da seção 7.

Aceite:
- [ ] Todas as operações funcionam no navegador do computador (via Tailscale); dashboard e controle ao vivo também no celular (D21).
- [ ] Comando ao vivo (pular, volume) responde em ≤ 2 s.
- [ ] Dashboard mostra status, música atual e Bluetooth em tempo real.

### Etapa 5 — Ofertas
Entrega: tela de Ofertas, editor de duas colunas, perfil de ingestão de ofertas, SpotScheduler, ducking, registro de execuções.

Aceite:
- [ ] ~~Oferta ancorada entre duas músicas toca exatamente entre elas, em volume cheio.~~ Pela D24 e D26 (i): nenhuma oferta criada pelo painel toca com a música parada (o modo entre músicas segue coberto pelos testes automatizados do agente).
- [ ] Oferta ancorada durante uma música toca no tempo marcado (±2 s), com a música a 30% e retorno suave.
- [ ] Reordenar a playlist move a oferta junto com a música ancorada.
- [ ] Regra "a cada 15 minutos" e regra de horário fixo disparam corretamente durante 1 dia inteiro.
- [ ] Duas ofertas disparadas juntas não se sobrepõem; a segunda respeita o intervalo mínimo.
- [ ] Oferta vencida não toca, inclusive com o Hub desligado no momento do vencimento.
- [ ] Oferta restrita à Loja 2 não toca nas outras lojas.
- [ ] Painel mostra a contagem de execuções por loja.

### Etapa 6 — Segurança, instalador e atualização
Entrega: instalador, scripts, ACL, atualização assinada. ~~Alertas no Telegram~~ (adiado pela D21).

Aceite:
- [ ] Instalação limpa em PC de teste seguindo só o `checklist-instalacao-loja.md`.
- [ ] Pacote com assinatura inválida é rejeitado.
- [ ] Atualização com versão quebrada é revertida automaticamente.
- [ ] ~~Alerta de Bluetooth desconectado chega no Telegram e a mensagem de "resolvido" também.~~ (adiado pela D21)
- [ ] Loja não consegue acessar outra loja pela rede Tailscale.
- [ ] Varredura de portas na loja (a partir de outra máquina) não encontra o agente.

### Etapa 7 — Piloto (1 loja, 7 dias)
Aceite:
- [ ] Música tocando em ≥ 99% do horário de funcionamento.
- [ ] Nenhuma intervenção presencial necessária.
- [ ] Nenhuma reclamação dos funcionários sobre lentidão do PC ou da internet.

### Etapa 8 — Implantação nas demais lojas
- [ ] Loja 2 e Loja 3 instaladas e aceitas pelo checklist da seção 16.3.

---

## 16. Testes e conferência

### 16.1 Testes automatizados (obrigatórios)

| Área | O que testar |
|---|---|
| Contracts | Serialização/desserialização do manifesto; hash canônico estável |
| Hub | Geração de manifesto; versão só sobe quando o conteúdo muda; enrollment; autorização das rotas; Range nos downloads; validação de agenda sem sobreposição |
| Ingestão | Arquivo inválido rejeitado; duplicata detectada; saída com loudness dentro de ±1 LU do alvo; fade-in/fade-out aplicados em músicas e não em ofertas |
| Agente | Diferença manifesto × cache; nunca ativar pendente incompleto; nunca apagar arquivo referenciado; retomada de download; regras de reprodução da seção 10.2 com relógio simulado; linha do tempo da mixagem (início a `crossfadeS` do fim, potência constante, faixas curtas e exceções da seção 10.2.1) |
| SpotScheduler | Prioridade; intervalo mínimo; fila máx. 3 e descarte após 10 min; validade com relógio simulado; âncoras presas ao item; `offsetMs` limitado; linha do tempo do ducking (800 ms / 1.500 ms) |
| Painel | Fluxos principais (login, editar playlist, editor de duas colunas, criar oferta, comando ao vivo) |

### 16.2 Testes de caos (manuais, antes do piloto)

- [ ] Puxar o cabo de rede da loja por 2 h no meio do expediente.
- [ ] Desligar o PC da loja da tomada com música tocando e religar.
- [ ] Desligar o Hub por um dia inteiro.
- [ ] Encher o disco da loja até sobrar < 500 MB.
- [ ] Mudar a hora do Windows para frente/trás 3 h e depois corrigir.
- [ ] Desligar a caixa por 1 h e religar.
- [ ] Remover o adaptador USB e reconectar.
- [ ] Enviar 30 alterações de playlist em sequência rápida.
- [ ] Desligar a caixa no meio de uma oferta.
- [ ] Deixar uma oferta vencer durante o expediente com a loja sem internet.

### 16.3 Checklist de aceite de cada loja

- [ ] Música começa sozinha após reiniciar o PC (sem login).
- [ ] Caixa reconecta após ser desligada e religada.
- [ ] Painel mostra a loja online, Bluetooth 🟢 e sincronizada.
- [ ] Alteração de playlist no painel aplicada na loja.
- [ ] Pular e volume pelo celular funcionam.
- [ ] Oferta de teste toca entre músicas e durante uma música, com a música a 30%.
- [ ] Oferta vencida não toca.
- [ ] Conta do funcionário não é administradora e não consegue parar o serviço.
- [ ] Sons do PC do caixa não saem na caixa da loja.
- [ ] Passagem de uma música para outra sem silêncio nem corte seco.
- [ ] ~~Alerta de teste do Telegram recebido.~~ (adiado pela D21)

---

## 17. Operação, monitoramento e manutenção

| Situação | Ação |
|---|---|
| Loja offline no painel | Verificar internet da loja; se a música estiver tocando, não há urgência |
| Bluetooth 🔴 persistente | Painel → "Reconectar Bluetooth"; se falhar, Área de Trabalho Remota pelo Tailscale e verificar o adaptador; em último caso, pedir para alguém desligar e religar a caixa |
| Música não atualiza | Ver progresso de sincronização e eventos da loja no painel |
| Atualizar o agente | Publicar release assinada → atualizar loja piloto → aguardar 1 dia → atualizar as demais |
| Trocar a caixa de som | Parear a nova no Windows (presencial ou remoto) → configuração local → escolher a nova caixa |
| PC da loja formatado | Reinstalar seguindo a seção 14.1 e o checklist; gerar novo código de vinculação (o token antigo é revogado automaticamente) |
| Restaurar o Hub | Reinstalar o Hub e copiar de volta a pasta `data\` do backup |

`docs/runbook.md` DEVE detalhar cada procedimento passo a passo.

---

## 18. Registro de decisões

| # | Decisão | Motivo |
|---|---|---|
| D1 | Sem streaming; manifesto + cache local | Banda mínima e loja independente da rede |
| D2 | Hub próprio em vez de Supabase/Firebase | Plano gratuito do Supabase tem 1 GB de arquivos e pausa o projeto após 7 dias sem uso; inviável para um sistema que roda sem intervenção |
| D3 | Tailscale | VPN sem abrir portas; plano Personal gratuito (6 usuários, dispositivos de usuário ilimitados, 50 recursos com tag) atende com folga |
| D4 | Agente no PC Windows já existente na loja, não em Raspberry Pi | Decisão do proprietário |
| D5 | C#/.NET para Hub e agente | APIs nativas do Windows para Bluetooth/áudio/serviço; contratos compartilhados |
| D6 | mpv como player | MPD é mal mantido no Windows; mpv permite escolher o dispositivo de saída |
| D7 | AAC 160 kbps `.m4a` | Compatibilidade universal na pré-escuta do painel (inclusive iPhone) |
| D8 | Conexões sempre iniciadas pela loja | Nenhuma porta aberta no PC compartilhado |
| D9 | Adaptador USB mantido | Instabilidade pontual é aceitável para o negócio; recuperação automática cobre a maior parte dos casos |
| D10 | Ofertas em um segundo processo mpv, misturado pelo Windows | Ducking sem processamento de áudio próprio; o player de música não é interrompido |
| D11 | Âncoras presas ao item da playlist e só em playlist sequencial | Reordenar mantém a oferta junto da música; no aleatório não existe posição fixa |
| D12 | Ducking padrão de 30% com rampas de 800 ms / 1.500 ms e locução a −14 LUFS | Voz inteligível sem corte seco da música |
| D13 | O `contentHash` do manifesto exclui `version`, `contentHash` e `generatedAt` (JSON canônico: chaves em ordem ordinal, sem espaços) | Com `generatedAt` no hash, a regeneração diária das 00:05 incrementaria a versão de todas as lojas sem mudança real |
| D14 | Guardião Bluetooth com estado "Verificando" (4 s após cada método antes de contar falha), checagem do endpoint a cada 2 s durante o backoff, e nova tentativa do nível 1 após os níveis 2 e 3 | Detalha a seção 9.1: caixa que volta sozinha é detectada sem esperar o backoff de 60 s (meta de 30 s); reiniciar `bthserv` ou o adaptador devolve o rádio no último estado salvo |
| D15 | Hash SHA-256 de binários de terceiros (mpv, ffmpeg) gravado em `scripts/tools.lock.json` e versionado; o valor vem do digest exibido na página de releases do GitHub ou, se não houver, de conferência humana (`download-tools.ps1 -AcceptNewHash`) | Os builds do mpv para Windows são de terceiros; todo download (scripts e CI) é verificado contra o lock |
| D16 | Fade-in de 0,5 s e fade-out de 3 s na ingestão das músicas, e mixagem (crossfade) de 5 s entre músicas no agente, com dois processos mpv de música alternados (seções 6.5, 10.1 e 10.2.1) | Decisão do proprietário: músicas cortadas no fim e transição natural, sem parar a música. Reaproveita as rampas de volume e a mistura de fluxos pelo Windows (D10) |
| D17 | `duckLevel` é fração de amplitude; o volume do mpv durante a oferta é volume da loja × ∛(duckLevel/100) | O mpv aplica curva cúbica ao volume. Com a conta linear (70 × 30% = 21) a música caía cerca de 31 dB e parecia parar durante a oferta no teste do PC piloto (23/10/2026) |
| D18 | Etapas 2 e 3 desenvolvidas (código e testes automatizados) em paralelo ao aceite da Etapa 1 no PC piloto, em vez de esperar o aceite completo. O aceite de cada etapa continua exigindo todos os critérios da seção 15 | Decisão do proprietário (23/10/2026): o teste no PC piloto já mostrou música e oferta tocando como serviço LocalSystem. Se o plano B da Etapa 1 for acionado, o player e o guardião mudam de processo, mas a lógica continua a mesma |
| D19 | Detalhes do agente offline (Etapa 2): (a) agenda, transições e ofertas avaliadas num laço único a cada 20 ms durante rampas e 250 ms no resto, em vez do `Scheduler` a cada 15 s, para o fim de expediente e a mixagem caírem no instante certo; (b) janela com fim `24:00` vai até a meia-noite e fim menor que o início atravessa a meia-noite; (c) `next` e `playNow` sempre usam o outro mpv de música com rampa de 1 s, mesmo com mixagem em 0; (d) a senha local é definida na instalação (`install-agent.ps1` ou `--set-local-password`), não na primeira abertura da página; sem senha a página não permite login; (e) `--build-manifest` gera um manifesto local a partir de pastas de músicas e ofertas, com `durationMs` 0 (a duração vem do mpv ao tocar); (f) a página local só aceita conexões de 127.0.0.1 com Host `127.0.0.1`/`localhost` e exige o cabeçalho `X-MarumbiSom` e Origin local em toda ação; (g) `SpotScheduler` e ofertas do manifesto local adiantados para a Etapa 2 | (a) a seção 10.2 pede fade de 3 s no fim do horário e crossfade de 5 s, impossíveis de acertar com resolução de 15 s; (b) horários de madrugada e expediente até meia-noite; (c) pular deve ser imediato sem silêncio; (d) uma página sem senha na primeira abertura poderia ser tomada pelo funcionário, que usa o mesmo PC; (e) a Etapa 2 não tem Hub para processar as músicas; (f) evita que um site aberto no navegador da loja comande o agente (CSRF e DNS rebinding); (g) as ofertas já vinham da Etapa 1 (D10, D16, D17) e as regras da seção 10.3 dependem do player |
| D20 | Detalhes do Hub e da sincronização (Etapa 3): (a) rotas além da seção 11.2: `GET /api/auth/me` (sessão do painel), `GET /api/uploads` (andamento da fila de ingestão, consultado pelo painel enquanto não há WebSocket do painel), `DELETE /api/stores/{id}/token` (revoga o token e derruba a conexão da loja) e `GET /healthz`; (b) chaves do manifesto: playlist `p{id}`, item com UUID estável, oferta `of-{id}`, posicionamento `pl-{id}`; a playlist ganha o campo opcional `name`, mostrado na página Configurar Som; (c) qualquer alteração regenera o manifesto de todas as lojas (a versão só sobe onde a `contentHash` mudou), e uma agenda com horários sobrepostos no mesmo dia é recusada; (d) o primeiro usuário do painel é criado pelo `install-hub.ps1` (`MarumbiSom.Hub.exe --create-admin`, senha de no mínimo 10 caracteres), não no primeiro acesso ao painel; (e) o painel exige o cabeçalho `X-MarumbiSom: 1` em toda escrita, além do cookie `SameSite=Strict`; (f) `GET /agent/files/{hash}` só entrega arquivos do manifesto mais recente da própria loja; (g) na ingestão, o número da faixa no início do nome do arquivo (`01 - `) é descartado antes de separar `Artista - Título`, e a codificação é bit a bit determinística (`-fflags +bitexact`, sem metadados) para o mesmo áudio gerar o mesmo hash; os uploads esperam em `incoming\` e voltam para a fila se o Hub reiniciar; (h) no agente, a versão do manifesto é comparada por loja: um manifesto de outra loja (vinculação nova ou o manifesto local do `--build-manifest`) substitui o ativo mesmo com número menor, sempre depois de 100% dos arquivos baixados e conferidos; (i) arquivos que faltam no manifesto ativo também são baixados a qualquer momento (não só na inicialização); arquivo que o Hub responde 404 é tentado de novo em 10 min; (j) o endereço do Hub precisa ser `https` (Tailscale Serve), exceto `http` para o próprio computador em testes; o token fica em `config.json` (seção `Hub`) protegido por DPAPI de escopo da máquina, e a página Configurar Som também permite desvincular; (k) o Hub roda com a conta virtual `NT SERVICE\MarumbiSom.Hub`, e `C:\MarumbiSom\Hub` perde a herança de permissões da raiz do disco; (l) `ffmpeg.exe` e `ffprobe.exe` (build 8.0.1 essentials de gyan.dev, com SHA-256 no `tools.lock.json`) vêm no pacote do Hub montado pelo CI com `download-tools.ps1 -For hub`; o `install-hub.ps1` só confere que estão lá | (a) o painel visual é da Etapa 4, e o andamento da ingestão precisa aparecer antes disso; revogar o token é a resposta a um PC de loja perdido; `/healthz` serve ao instalador; (b) nomes de playlist mudam, o id não; o UUID do item é o que as âncoras de ofertas precisam (seção 6.3); (c) com 3 lojas, regenerar todas custa milissegundos e elimina o risco de esquecer uma dependência; agenda sobreposta deixaria a escolha da playlist ambígua; (d) uma tela de primeiro acesso poderia ser tomada por quem abrisse o painel primeiro na rede Tailscale; (e) proteção extra contra CSRF caso o cookie vaze para outro site; (f) um token vazado não baixa a biblioteca inteira; (g) arquivos de CD vêm como `01 - Artista - Título`; sem codificação determinística a detecção de duplicatas do passo 7 da seção 6.5 nunca acertaria; (h) sem isso a loja que testou com o manifesto local nunca aceitaria o primeiro manifesto do Hub; (i) cumpre a regra 1 também quando um arquivo some com o agente ligado; (j) a seção 5 exige HTTPS pelo Tailscale; o serviço roda como LocalSystem, então a DPAPI de usuário não serviria; (k) a raiz do C:\ dá permissão de escrita a usuários autenticados, o que permitiria trocar os binários do serviço; o Hub não precisa de administrador; (l) mesmo modelo do mpv no agente (D15): download verificado e repetível, sem depender da internet na hora de instalar |
| D21 | (a) O painel (Etapa 4) é **desktop first**: as telas de edição (biblioteca, playlists, ofertas, agenda, configurações) são pensadas para o navegador do computador; no celular, o dashboard e o controle ao vivo das lojas continuam utilizáveis em 360 px. (b) Os alertas pelo Telegram (seção 6.6, tela de Configurações, Etapa 6 e checklists) ficam fora da v1 inicial; as mesmas condições aparecem no dashboard | Decisão do proprietário (23/10/2026): "vou querer ter como editar via desktop principalmente e não preciso de notificação via telegram, pelo menos nada disso inicialmente". O Telegram pode voltar numa versão futura sem mudar o agente: as condições já chegam ao Hub pelo status |
| D22 | Etapa 4 (painel) desenvolvida em paralelo aos testes de campo das Etapas 1 a 3 no PC piloto. O aceite de cada etapa continua exigindo todos os critérios da seção 15 | Decisão do proprietário (23/10/2026, "Pode começar"): o painel roda só no Hub e não depende do PC da loja; o reinício sem login no PC piloto já tocou música pela caixa Bluetooth na sessão 0, então o plano B da Etapa 1 não será necessário |
| D23 | Detalhes do painel (Etapa 4): (a) divisão com as próximas etapas: a tela de Ofertas, a coluna de ofertas do editor de playlist, "tocar oferta agora" e a próxima/última oferta no dashboard chegam com a Etapa 5; a versão do agente para distribuição e "atualizar agente" chegam com a Etapa 6. Até lá essas partes aparecem no painel como "em breve"; (b) a mensagem `status` do agente (seção 11.4) ganha os campos opcionais `playlistId` (playlist da agenda em vigor) e `queue` (hashes das próximas até 10 faixas), usados no dashboard e na fila da tela da loja; o Hub guarda também o instante da posição, da última mudança do Bluetooth e do último avanço da sincronização; (c) rotas além da seção 11.2: `GET /api/stores/{id}/tracks` (músicas do manifesto aplicado, para "tocar agora"), `DELETE /api/stores/{id}` (remove a loja, com confirmação), `POST /api/auth/password` (trocar senha) e `GET /openapi/v1.json` (com login); `GET /api/settings` traz o status do backup e a versão do Hub, e `GET /api/stores/{id}/history` aceita `since`; (d) `/ws/panel` (com login e conferência da origem) envia `store` (resumo atualizado de uma loja a cada status, conexão ou versão nova) e `changed` (tópicos como `tracks`, `uploads`, `playlists`, `events:{id}`, `history:{id}`); o painel atualiza o cache do TanStack Query, reconecta sozinho e recarrega tudo ao reconectar; (e) as condições da seção 6.6 (sem sinal > 10 min, Bluetooth desconectado > 5 min, sincronização parada > 60 min, disco < 1 GB, mais de 3 reinícios do agente em 1 h, só dentro do horário de funcionamento) são calculadas pelo Hub e aparecem como alertas no cartão da loja; o reinício do agente é detectado pela queda do `uptimeS` e vira o evento `agent.restarted`; (f) o Hub serve o build do painel (`wwwroot`, montado pelo CI) em `/`, com volta para `index.html` em rotas do painel (nunca em `/api`, `/agent`, `/ws`), cache longo só para `/assets` e `Content-Security-Policy` restrita à própria origem; (g) o OpenAPI do Hub é gerado pelo `Microsoft.AspNetCore.OpenApi` e gravado em `web/panel/openapi.json` (um teste falha se ele estiver desatualizado); o `openapi-typescript` gera os tipos e o `openapi-fetch` faz as chamadas; (h) a agenda da loja é editada como lista de faixas (dias, início, fim, playlist) com a grade semanal ao lado como visualização; a playlist é editada localmente e só vai para as lojas no botão "Salvar e publicar" (regra "salvar = publicar" da seção 7.1) | (a) as ofertas e a atualização remota são entregas das Etapas 5 e 6; (b) a seção 7.2 pede a fila e a barra de progresso, e o agente já calcula a fila para a página Configurar Som; (c) as telas da seção 7.2 precisam desses dados, e o painel não pode exigir acesso ao banco; (d) a seção 7.1 proíbe recarregar a página para ver o estado; enviar o resumo pronto evita uma consulta por mensagem; (e) cumpre a D21: as mesmas condições, sem Telegram, e prontas para um envio futuro; (f) uma instalação só (o Hub) e nenhum script externo no painel; (g) a seção 7.3 exige tipos gerados do OpenAPI; (h) no computador a lista é mais rápida de editar e a grade mostra buracos e sobreposições de relance |
| D24 | Ofertas sempre por cima da música, com ducking (decisão do proprietário em 23/10/2026: "não quero nunca, a princípio, uma oferta tocando sozinha"): (a) o painel da Etapa 5 só cria ofertas `during`: `everyMinutes` e `clock` com `mode = during` e âncora só `anchorDuring` (soltar a oferta em cima de uma música); o espaço entre músicas não aceita oferta; (b) `everyTracks`, que é sempre `between`, fica fora do painel enquanto esta decisão valer; se o proprietário quiser "a cada N músicas", ela volta na Etapa 5 como variante `during`; (c) o contrato, o Hub e o agente continuam aceitando `between` e `anchorBetween` (o `--build-manifest --offer-every-tracks` segue gerando `between` para testes); (d) revisitar se o proprietário pedir | No teste do PC piloto a oferta entre músicas tocou sozinha, com a música parada, e o proprietário não gostou; a oferta durante a música com ducking (teste 8 do roteiro da Etapa 2) foi aprovada |
| D25 | Saída padrão do Windows e oferta cortada (seção 9.5, ajuste de campo): (a) o `AudioEndpointManager` só restaura a saída padrão para uma saída ativa fora do contêiner da caixa (o endpoint "Viva-voz" da caixa conta como a própria caixa, e também é restaurado se virar o padrão); (b) se o Windows devolver a saída padrão para a caixa três vezes seguidas, o agente registra um aviso e não mexe mais nela até a caixa reconectar; (c) oferta que termina mais de 1,5 s antes da duração informada pelo mpv é registrada como `interrupted`, com o tempo que tocou no log; (d) avisos e erros do próprio mpv (`request_log_messages warn`) vão para o log do agente | No PC piloto (23/10/2026) a restauração não pegava e se repetia a cada 10 s, e uma oferta entre músicas foi cortada no meio e registrada como tocada. Restaurar para o viva-voz abre o perfil de chamada e corta o áudio estéreo; brigar com o Windows não resolve e cada troca arrisca um corte |
| D26 | Detalhes das ofertas (Etapa 5): (a) o áudio da oferta é enviado antes, por `POST /api/tracks` com `kind = offer` (o mesmo envio da Biblioteca, com barra de andamento e o perfil de ofertas da seção 6.5); `POST /api/offers` recebe JSON com o `audioHash` já processado, e `PATCH /api/offers/{id}` pode trocar o áudio; (b) `POST` e `PATCH /api/offers` aceitam também as regras (`rules`), e `PUT /api/offers/{id}/rules` substitui só as regras; o painel cria só `everyMinutes` e `clock` com `mode = during` (D24), com restrição opcional a playlists; (c) `GET /api/playlists/{id}` traz as âncoras da playlist, e `PUT /api/playlists/{id}/anchors` recebe `{ offerId, itemId, offsetMs }` (âncora `anchorDuring`); o Hub recusa âncora em playlist aleatória, item de outra playlist e `offsetMs` fora de 0 a duração − 5 s; o editor salva primeiro os itens e depois as âncoras, já com os ids dos itens novos; (d) a mensagem `status` do agente ganha o campo opcional `nextSpot { offerId, at }`: a próxima oferta prevista pelas regras, pelas âncoras da música atual e das próximas da fila, ou já na fila de ofertas; o Hub guarda a prevista e tira a última tocada de `offer_plays`, e o resumo da loja traz as duas (`nextOffer`, `lastOffer`); (e) a situação da oferta é calculada pelo Hub (desativada, vencida, agendada, no ar), com `expiresSoon` quando ela vence nas próximas 24 h; (f) `GET /api/offers/{id}/plays` e a lista de ofertas trazem as execuções dos últimos 7 dias por loja (tocou, interrompida, descartada); (g) `GET /api/stores/{id}/offers` lista as ofertas do manifesto aplicado que valem agora, para "tocar oferta agora", e o histórico da loja (`/history`) traz as ofertas junto com as músicas; (h) remover uma oferta apaga as regras, as âncoras e as lojas dela, e as execuções ficam no histórico; (i) o critério de aceite "oferta ancorada entre duas músicas" vale só para o contrato e os testes do agente; no painel ele passa a ser "nenhuma oferta criada pelo painel toca com a música parada"; (j) tudo que envia, baixa ou processa mostra barra de andamento, e cards e linhas de lista abrem a tela com um clique em qualquer parte (pedido do proprietário em 24/10/2026) | (a) o processamento é assíncrono e leva alguns segundos; enviar antes reaproveita a barra de andamento, e a oferta nunca nasce sem áudio; (b) uma oferta com regras é salva de uma vez; (c) a âncora aponta para o id estável do item, que só existe depois de salvo; (d) a seção 7.2 pede a próxima oferta prevista, e só o agente conhece a fila e os contadores das regras; (e) a (g) telas da seção 7.2; (h) o histórico de execuções é a prova de veiculação; (i) D24; (j) pedido do proprietário depois do teste da Etapa 4 |

Novas decisões DEVEM ser adicionadas aqui com número sequencial.

---

## 19. Riscos conhecidos

| Risco | Probabilidade | Impacto | Mitigação |
|---|---|---|---|
| Áudio/rádio não funcionam a partir da sessão 0 | Média | Alto | Etapa 1 valida primeiro; plano B documentado |
| Driver do adaptador USB instável | Média | Médio | Escalonamento 9.2/9.3; alertas; troca do adaptador se recorrente |
| `IPolicyConfig` (não documentada) quebrar após atualização do Windows | Baixa | Baixo | Falha registrada; música continua indo só para a caixa |
| PC da loja lento ou com pouco disco | Baixa | Médio | Limite de banda, limite de cache, alerta de disco |
| Atualização do Windows reiniciar no expediente | Média | Baixo | Horário ativo; serviço sobe sozinho |
| Funcionário com conta de administrador | Média | Médio | Script de hardening alerta; checklist exige conta padrão |
| Mudança nos planos gratuitos do Tailscale | Baixa | Médio | Alternativa gratuita: Headscale (servidor de coordenação próprio, open source) |
| Até três fluxos simultâneos no endpoint Bluetooth (mixagem + oferta) causarem falha ou atraso | Baixa | Médio | Dois fluxos validados na Etapa 1; três na Etapa 2 |
| Preço vencido anunciado na loja | Média | Alto | Validade conferida no Hub e no agente (regra 14); aviso de vencimento em 24 h no painel |
| Âncoras perdidas ao remover músicas | Média | Baixo | Aviso obrigatório no painel antes de salvar |
| Perda do PC do escritório | Baixa | Médio | Lojas seguem tocando; backup externo da pasta `data\` |

---

## 20. Aspecto legal

- Música tocada em estabelecimento comercial é **execução pública**. No Brasil, exige licença e pagamento ao **ECAD**, independentemente da origem do arquivo ou de o sistema ser próprio.
- Arquivos baixados de plataformas de streaming ou vídeo, em regra, não dão direito a esse uso.
- Alternativa: acervo de música licenciada para uso comercial ou livre de direitos. Dependendo do acervo, pode reduzir ou dispensar o pagamento ao ECAD; verificar caso a caso.
- O sistema NÃO inclui ferramenta de download de plataformas (seção 1.2).

---

## 21. Glossário

| Termo | Significado |
|---|---|
| Hub | Servidor central do escritório |
| Agente | Programa que roda como serviço no PC de cada loja |
| Manifesto | Documento JSON com o estado desejado da loja (playlists, ordem, agenda, volume) |
| Cache | Pasta da loja com as músicas já baixadas |
| Hash / SHA-256 | Impressão digital do conteúdo de um arquivo; identifica cada música |
| Tailscale | Rede privada virtual entre escritório, lojas e celular |
| Tailscale Serve | Recurso do Tailscale que publica o Hub em HTTPS só dentro da rede privada |
| A2DP | Perfil Bluetooth de áudio estéreo usado pela caixa |
| Endpoint de áudio | Dispositivo de saída de som no Windows |
| Sessão 0 | Sessão isolada do Windows onde os serviços rodam |
| DPAPI | Criptografia nativa do Windows para guardar segredos na máquina |
| Enrollment / vinculação | Processo de registrar uma loja no Hub com código de uso único |
| LUFS | Unidade de volume percebido usada na normalização |
| Oferta (spot) | Áudio curto de locução promocional tocado entre músicas ou por cima delas |
| Ducking | Abaixar automaticamente a música enquanto uma oferta toca |
| Âncora | Posição fixa de uma oferta presa a uma música específica de uma playlist |
| Regra de oferta | Recorrência automática: a cada N músicas, a cada X minutos ou em horário fixo |
| Mixagem (crossfade) | Transição em que a próxima música começa enquanto a atual termina, com os volumes trocando em rampa |

---

## 22. Fontes

- Tailscale — preços e plano Personal: https://tailscale.com/pricing
- Análise dos limites do plano gratuito do Tailscale (agosto/2026): https://www.ssdnodes.com/learn/is-tailscale-free-plan-limits
- Supabase — preços e limites do plano gratuito: https://supabase.com/pricing
- Tailscale Serve: https://tailscale.com/kb/1312/serve
- mpv — manual (IPC e `--audio-device`): https://mpv.io/manual/stable/
- ffmpeg — filtro `loudnorm`: https://ffmpeg.org/ffmpeg-filters.html#loudnorm
- Windows — `Windows.Devices.Radios`: https://learn.microsoft.com/uwp/api/windows.devices.radios
- Windows — `BluetoothSetServiceState`: https://learn.microsoft.com/windows/win32/api/bluetoothapis/nf-bluetoothapis-bluetoothsetservicestate
- Windows — propriedades `KSPROPSETID_BtAudio`: https://learn.microsoft.com/windows-hardware/drivers/audio/kspropsetid-btaudio
- ECAD — execução pública: https://www4.ecad.org.br
