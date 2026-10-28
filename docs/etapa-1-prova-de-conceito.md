# Etapa 1 — Prova de conceito no PC piloto

> **A partir da Etapa 2** o modo de teste deste roteiro (pasta `poc\musicas`, oferta a cada 2 min) não existe
> mais. Para repetir os testes abaixo com a versão atual, monte a configuração com `--build-manifest` e uma
> oferta a cada 2 min (`--offers <pasta> --offer-every-minutes 2 --min-gap 0`), como no
> [roteiro da Etapa 2](etapa-2-agente-offline.md). Os critérios de aceite continuam os mesmos.

Objetivo (seção 15): provar que um serviço Windows rodando como **LocalSystem**, sem ninguém logado, consegue
ligar o Bluetooth, conectar a caixa, tocar música e misturar uma oferta com ducking. É a etapa de maior risco;
nada das etapas seguintes deve começar antes de todos os critérios abaixo estarem ✅.

## O que esta versão faz

- **Guardião Bluetooth** (seção 9): liga o rádio (níveis 1→2→3), conecta a caixa (métodos A→B→C) com espera
  de 5, 10, 20, 40 e 60 s, e detecta quando a caixa cai ou volta.
- **AudioEndpointManager** (seção 9.5): fixa o volume do endpoint da caixa em 100% e devolve a saída padrão do
  Windows para o alto-falante do PC quando o Windows troca para a caixa.
- **Reprodução de teste**: toca em loop as músicas de `C:\ProgramData\MarumbiSom\poc\musicas`, com a próxima
  faixa pré-carregada, e a cada 2 minutos toca `C:\ProgramData\MarumbiSom\poc\oferta.m4a` no segundo mpv com
  ducking (música 100% → 30% em 800 ms, oferta, volta em 1.500 ms).
- Watchdog do mpv: processo morto volta em ~2 s; posição parada por 30 s → reinicia.

## Pré-requisitos (humano, seção 14.1)

1. Conta de **administrador** com senha forte e conta **padrão** para o funcionário.
2. Adaptador Bluetooth USB direto numa porta traseira do PC; caixa a até ~5 m.
3. Caixa pareada em *Configurações → Bluetooth e dispositivos → Adicionar dispositivo*.
4. .NET não precisa estar instalado: o pacote é *self-contained*.

## Preparar o pacote (no PC de desenvolvimento)

```powershell
dotnet publish src\MarumbiSom.Agent -c Release -r win-x64 --self-contained -o publish\agent
# Baixa o mpv conferindo o SHA-256 gravado em scripts\tools.lock.json:
.\scripts\download-tools.ps1
```

Alternativa sem compilar: baixe o artefato **MarumbiSom.Agent-win-x64** da última execução do CI em
*Actions*. Ele já vem com `tools\mpv.exe` (hash conferido) e a pasta `scripts\`.

## Instalar no PC piloto (PowerShell como administrador)

```powershell
Set-ExecutionPolicy -Scope Process Bypass
.\scripts\harden-store-pc.ps1 -EmployeeAccount "Caixa"     # confira o relatório gerado
.\publish\agent\MarumbiSom.Agent.exe --list-devices         # anote o endereço (MAC) da caixa
.\scripts\install-agent.ps1 -SourceDir .\publish\agent -SpeakerAddress AA:BB:CC:DD:EE:FF
```

Copie 5 a 10 músicas para `C:\ProgramData\MarumbiSom\poc\musicas` e um áudio curto (5–20 s) como
`C:\ProgramData\MarumbiSom\poc\oferta.m4a`. Logs: `C:\ProgramData\MarumbiSom\logs\agent-AAAAMMDD.log`.

Para ajustar intervalos de teste sem recompilar, edite `C:\ProgramData\MarumbiSom\config.json`, por exemplo:

```json
{ "Bluetooth": { "SpeakerAddress": "AA:BB:CC:DD:EE:FF" }, "Poc": { "SpotIntervalMinutes": 1 } }
```

e reinicie o serviço (`Restart-Service MarumbiSom.Agent`).

## Critérios de aceite (seção 15, Etapa 1)

Anote o resultado de cada um e, se falhar, o trecho do log correspondente.

| # | Teste | Meta | Resultado |
|---|---|---|---|
| 1 | Reiniciar o PC 5 vezes seguidas **sem fazer login** | música em ≤ 120 s após o Windows iniciar | |
| 2 | Desligar e religar a caixa 5 vezes | reconecta em ≤ 30 s | |
| 3 | Desligar o Bluetooth pelo menu do Windows | religado em ≤ 15 s | |
| 4 | Desconectar a caixa pelo menu do Windows | reconectada em ≤ 30 s | |
| 5 | Desativar o adaptador no Gerenciador de Dispositivos | reativado e reconectado | |
| 6 | Tocar um som do Windows (ex.: e-mail, erro) | sai no alto-falante do PC, não na caixa | |
| 7 | Deixar tocando 30 min com oferta a cada 2 min | dois mpv sem falha nem atraso perceptível | |
| 8 | Ouvir a oferta | 100% → 30% → 100% suave, sem estalos | |
| 9 | Matar o `mpv.exe` no Gerenciador de Tarefas (como admin) | volta a tocar em ≤ 5 s | |
| 10 | Logado como funcionário, tentar parar o serviço | acesso negado | |

Dicas para medir: o log registra `Caixa conectada`, `Caixa desconectada`, `Tocando …` e cada ação de
recuperação com horário em milissegundos.

## Se o áudio ou o rádio não funcionarem a partir do serviço (sessão 0)

Sintomas: o log mostra "Caixa conectada" mas não sai som; ou `Acesso ao rádio negado pelo Windows`; ou o mpv
termina logo após iniciar. Antes de concluir, rode o mesmo exe **logado como administrador**, fora do serviço
(`Stop-Service MarumbiSom.Agent; & "C:\Program Files\MarumbiSom\MarumbiSom.Agent.exe"`): se funcionar assim e
não como serviço, é o problema da sessão 0 e vale o **plano B** da seção 15 (processo `MarumbiSom.AudioHost`
na sessão do usuário com logon automático). Nesse caso registre a decisão na seção 18 antes de implementar.

## Teste sem hardware

Veja o [roteiro da Etapa 2](etapa-2-agente-offline.md#teste-sem-hardware-qualquer-sistema-operacional): o modo
`--simulate` roda tudo com Bluetooth e mpv falsos (funciona também em Linux/macOS).
