# Etapa 6 — Segurança, instalador e atualização

Objetivo (seção 15): instalar uma loja do zero só pelo [checklist](checklist-instalacao-loja.md) e atualizar o
agente das lojas pelo painel, com pacote assinado e volta automática para a versão anterior se a nova não subir.
Os alertas pelo Telegram ficaram para depois (D21).

## O que esta versão faz

- **Instalador do agente** `MarumbiSom.Agent-Setup-<versão>.exe` (artefato **MarumbiSom.Agent-Setup** do CI):
  instala em `C:\Program Files\MarumbiSom`, registra o serviço e, na primeira vez, pede a senha da página
  Configurar Som numa janela do PowerShell. Rodar de novo atualiza sem perder configuração, senha e cache.
- **Chave de assinatura** (seção 12): a chave privada fica só no PC do administrador; a chave pública vai no
  repositório (`src/MarumbiSom.Agent/release-public-key.txt`) e sai em todo build do agente. Sem a chave pública o
  agente recusa qualquer atualização remota.
- **Versão do agente para distribuição** (painel → Configurações): envia o `.zip` e o `.sig.json` gerados pelo
  `sign-release.ps1` com barra de envio, mostra quantas lojas estão em cada versão e atualiza todas as lojas
  conectadas de uma vez. Na loja (Loja → Ajustes), **Atualizar agente** atualiza só aquela loja (use na piloto).
- **Atualização na loja**: o agente baixa em segundo plano (com o limite de banda da sincronização e retomada),
  confere tamanho, SHA-256 e assinatura e só aceita versão **mais nova** que a instalada. O painel mostra a barra
  do download no cartão da loja. O `MarumbiSom.Updater.exe` (rodando de fora da pasta do programa) para o serviço,
  guarda a versão atual, troca os arquivos e inicia o serviço. A música para por alguns segundos.
- **Volta automática**: se a versão nova não falar com o Hub em 2 minutos, o Updater restaura a anterior e
  inicia o serviço de novo. O painel mostra "Agente atualizado para X", "Versão X revertida" ou "Atualização
  para X falhou" com o motivo, e o evento fica em Loja → Eventos.
- **Rede**: a política do Tailscale (`infra/tailscale-acl.hujson`) só deixa a loja falar com o Hub na porta
  443; o agente só escuta em `127.0.0.1` e não abre porta nenhuma.

Arquivos da atualização no PC da loja, em `C:\ProgramData\MarumbiSom\updates`: `download\` (pacote baixado),
`backup\` (versão anterior), `updater.log` (o que o Updater fez) e `result.json` (último resultado).

## Gerar a chave (uma vez, no PC do administrador)

1. Baixe o artefato **MarumbiSom.Release-win-x64** do CI e extraia (ex.: `C:\MarumbiSom-release`).
2. No PowerShell (não precisa ser administrador):

```powershell
Set-ExecutionPolicy -Scope Process Bypass
cd C:\MarumbiSom-release
.\new-signing-key.ps1
```

3. A chave privada fica em `Documentos\MarumbiSom\chave-assinatura-atualizacoes.txt`, legível só pelo seu
   usuário. Copie para um pendrive guardado em local seguro. Ela nunca vai para o Git, o Hub, e-mail ou nuvem.
4. A chave pública aparece na tela. Ela entra no repositório em `src/MarumbiSom.Agent/release-public-key.txt`;
   os builds seguintes do CI já saem com ela.

## Publicar uma versão

```powershell
cd C:\MarumbiSom-release
.\sign-release.ps1 -AgentDir C:\Downloads\MarumbiSom.Agent-win-x64 -OutDir C:\MarumbiSom-pacotes
```

Envie os dois arquivos (`MarumbiSom.Agent-<versão>.zip` e `.sig.json`) em Configurações → Versão do agente para
distribuição → **Enviar versão…**. Passo a passo completo, com loja piloto primeiro, no [runbook](runbook.md).

## Critérios de aceite (seção 15, Etapa 6)

| # | Teste | Como fazer | Meta | Resultado |
|---|---|---|---|---|
| 1 | Instalação limpa | No PC de teste, remova o agente (`uninstall-agent.ps1 -RemoveData`) e instale seguindo só o [checklist](checklist-instalacao-loja.md) | Loja tocando e "Sincronizado" no painel, sem passos fora do checklist | |
| 2 | Atualização boa | Gere o pacote de um build mais novo que o instalado, envie no painel e use **Atualizar agente** na loja | Barra de download no cartão; música para por poucos segundos; "Agente atualizado para X" e a versão nova em Loja → Ao vivo | |
| 3 | Assinatura inválida | `sign-release.ps1 -AgentDir … -TestInvalidSignature -Version 0.6.998`, envie e atualize a loja | O painel mostra "Assinatura inválida… Atualização recusada"; a loja não baixa nada e segue tocando; evento `update.failed` | |
| 4 | Versão quebrada | `sign-release.ps1 -AgentDir … -TestBroken -Version 0.6.999`, envie e atualize a loja (fora do horário de funcionamento: a música fica parada ~2 min) | Em até ~3 min a loja volta sozinha para a versão anterior e toca; o painel mostra "Versão 0.6.999 revertida" | |
| 5 | Loja não acessa outra loja | Com dois dispositivos `tag:loja`, no PC da loja: `Test-NetConnection <IP Tailscale da outra loja> -Port 3389` e `-Port 445` | Todos `TcpTestSucceeded : False`; `Test-NetConnection <IP do Hub> -Port 443` dá `True` | |
| 6 | Varredura de portas | De outro PC (rede da loja e Tailscale): `Test-NetConnection <IP da loja> -Port 47800` (ou `nmap -p- <IP>`) | A porta 47800 não aparece em nenhum dos dois endereços; no PC da loja, `netstat -ano \| findstr 47800` mostra só `127.0.0.1:47800` | |
| 7 | Testes automatizados | CI do PR | Assinatura Ed25519, recusa de pacote adulterado, sem chave ou versão antiga, retomada do download, Updater com volta automática e proteção contra caminhos fora da pasta, ferramenta de assinatura, envio no painel e o teste ponta a ponta (enviar versão → atualizar loja) | ✅ |

Depois dos testes 3 e 4, em Configurações escolha a versão boa em **Usar esta** e remova as versões de teste.

Registros úteis no log da loja: `Atualização para a versão X pedida pelo Hub`, `Atualização para a versão X
recusada: motivo`, `Instalando a versão X`, `Versão X confirmada para o atualizador`. No `updater.log`:
`Atualizando o agente de A para B`, `Versão B confirmada pelo agente` ou `… Restaurando a versão A`.
