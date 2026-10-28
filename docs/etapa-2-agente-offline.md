# Etapa 2 — Agente offline completo

Objetivo (seção 15): o agente toca sozinho, sem Hub, a partir de um manifesto gravado no próprio PC: agenda,
playlists, mixagem entre músicas, ofertas, estado salvo e a página **Configurar Som**.

## O que esta versão faz

- **Player** (seções 10.1, 10.2 e 10.2.1): dois mpv de música alternados com mixagem *equal-power*, faixa
  curta mixando em um terço da duração, emenda sem pausa com mixagem em 0, pular/tocar agora em 1 s, fade-out
  de 3 s no fim do horário, troca de playlist ao terminar a faixa atual, pausa do administrador até a próxima
  janela, continuidade após reiniciar (`state.json`).
- **Ofertas** (seção 10.3): entre músicas (sem mixagem naquela transição) ou por cima da música com ducking;
  regras a cada N músicas e a cada N minutos; prioridade, intervalo mínimo, fila de 3, descarte após 10 min e
  validade.
- **Vigia**: mpv morto volta em ~2 s e a faixa continua do ponto em que estava; posição parada por 30 s →
  reinicia.
- **Configurar Som** (seção 8.4): página em `http://127.0.0.1:47800`, só neste computador, com senha. Mostra o
  status, escolhe a caixa pareada, toca o som de teste, mostra os últimos registros e troca a senha.
- **`--build-manifest`**: monta o manifesto a partir de pastas de músicas e ofertas (na Etapa 3 isso passa a
  vir do Hub).

O modo de teste da Etapa 1 (pasta `poc\musicas` e oferta a cada 2 min) foi removido.

## Instalar ou atualizar (PowerShell como administrador)

Baixe o artefato **MarumbiSom.Agent-win-x64** da última execução do CI deste PR em *Actions* e extraia, por
exemplo, em `C:\MarumbiSom-pacote`.

```powershell
Set-ExecutionPolicy -Scope Process Bypass
cd C:\MarumbiSom-pacote
.\scripts\install-agent.ps1 -SourceDir .
```

Na primeira vez o instalador pede a **senha da página Configurar Som** (mínimo 8 caracteres) e cria o atalho
**Configurar Som** na área de trabalho de quem instalou. Para trocar uma senha esquecida:
`.\scripts\install-agent.ps1 -SourceDir . -ResetLocalPassword`.

## Montar a configuração (manifesto local)

```powershell
$exe = "C:\Program Files\MarumbiSom\MarumbiSom.Agent.exe"

# Uma playlist o dia todo, com as ofertas tocando entre músicas a cada 3 músicas:
& $exe --build-manifest --music C:\Musicas --offers C:\Ofertas --offer-every-tracks 3

# Duas playlists com horário (a segunda com --add, para manter a primeira):
& $exe --build-manifest --music C:\Musicas\manha --playlist manha --start 08:00 --end 14:00 --offers C:\Ofertas --offer-every-minutes 15
& $exe --build-manifest --music C:\Musicas\tarde --playlist tarde --start 14:00 --end 20:00 --add
```

- As músicas tocam na ordem alfabética dos arquivos (`--shuffle` para aleatório). O nome
  `01 - Artista - Título.mp3` vira artista e título na página.
- Os arquivos são copiados para o cache (`C:\ProgramData\MarumbiSom\cache`); a pasta original pode ser
  apagada depois.
- O serviço aplica o manifesto novo em até 5 s, sem reiniciar e sem cortar a faixa atual.
- `& $exe --build-manifest` sem opções mostra todas as opções (volume, mixagem, dias da semana, ducking…).

## Critérios de aceite (seção 15, Etapa 2)

| # | Teste | Como fazer | Meta | Resultado |
|---|---|---|---|---|
| 1 | Agenda troca de playlist | Monte duas playlists com a troca daqui a ~5 min e o fim da segunda daqui a ~10 min. Ex.: às 15:02, `--playlist manha --start 15:00 --end 15:07` e depois `--playlist tarde --start 15:07 --end 15:12 --add` | Em 15:07 a música atual termina e a primeira da `tarde` entra com mixagem; em 15:12 fade-out de 3 s e silêncio | |
| 2 | Continuidade | Anote a música tocando e reinicie o PC | Volta na **música seguinte**, não na primeira | |
| 3 | Matar o mpv | Gerenciador de Tarefas (como admin) → finalizar um `mpv.exe` | Volta a tocar em ≤ 5 s | |
| 4 | Funcionário não para o serviço | Logado como funcionário: `services.msc` → Marumbi Som → Parar | Acesso negado | |
| 5 | Mixagem de 5 s | Ouvir 3 trocas de música | Sem silêncio, sem salto de volume, sem estalo | |
| 6 | Mixagem em 0 | Remonte com `--crossfade 0` e ouça 3 trocas | As faixas emendam sem pausa | |
| 7 | Oferta entre músicas | Com `--offer-every-tracks 2` | A música termina, a oferta toca, a próxima começa logo depois, **sem** mixagem nessa troca | |
| 8 | Oferta por cima da música | Com `--offer-every-minutes 2` | A música abaixa suave (~800 ms), a oferta toca, a música volta suave (~1,5 s) | |
| 9 | Configurar Som | Atalho na área de trabalho → senha → escolher a caixa → **Tocar som de teste** | Dois bipes na caixa; status e registros aparecem | |
| 10 | Senha errada | Errar a senha 5 vezes | Bloqueio de 15 min | |
| 11 | Testes automatizados | CI do PR | `Scheduler`, regras da seção 10.2 e ofertas passando | ✅ |

Registros úteis no log (`C:\ProgramData\MarumbiSom\logs\agent-AAAAMMDD.log` ou na página): `Tocando … (playlist)
no mpv A/B`, `Fim do horário às HH:mm: fade-out`, `Oferta '…' tocará ao fim de …`, `mpv A: … reiniciando`,
`Retomando … em N s`.

## Teste sem hardware (qualquer sistema operacional)

```bash
dotnet run --project src/MarumbiSom.Agent -- --build-manifest --music <pasta> --Agent:DataDirectory=/tmp/som
echo "senha-de-teste" | dotnet run --project src/MarumbiSom.Agent -- --set-local-password --Agent:DataDirectory=/tmp/som
dotnet run --project src/MarumbiSom.Agent -- --simulate --Agent:DataDirectory=/tmp/som
```

Abra `http://127.0.0.1:47800`, entre com a senha e escolha a "Caixa Simulada": o player falso começa a tocar
(cada música dura 20 s no modo simulado).
