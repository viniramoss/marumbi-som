# Runbook — operação do Marumbi Som

Procedimentos passo a passo da seção 17. Para instalar uma loja nova, use o
[checklist de instalação](checklist-instalacao-loja.md).

Regra de ouro: se a música está tocando na loja, nada é urgente. A loja toca do cache local mesmo sem internet,
sem Hub e sem painel.

## Onde fica cada coisa

| O quê | Onde |
|---|---|
| Painel | `https://<hub>.<tailnet>.ts.net` (usuário `admin`) |
| Hub (PC do escritório) | serviço `MarumbiSom.Hub`; programa em `C:\MarumbiSom\Hub\app`, dados em `C:\MarumbiSom\Hub\data`, logs em `C:\MarumbiSom\Hub\logs\hub-AAAAMMDD.log` |
| Atualizações do Hub | tarefa agendada **MarumbiSom Hub - atualizador**; log em `C:\MarumbiSom\Hub\updater\hub-updater.log`, cópias do banco de antes de cada troca em `data\updates\banco-antes` (D32) |
| Backups do banco | `C:\MarumbiSom\Hub\data\backups\marumbi-AAAA-MM-DD.db` (todo dia às 02:00, 30 dias) |
| Agente (PC da loja) | serviço `MarumbiSom.Agent` ("Marumbi Som - Agente da loja"); programa em `C:\Program Files\MarumbiSom`, dados em `C:\ProgramData\MarumbiSom` |
| Logs da loja | `C:\ProgramData\MarumbiSom\logs\agent-AAAAMMDD.log` ou atalho **Configurar Som** → Últimos registros |
| Atualizações da loja | `C:\ProgramData\MarumbiSom\updates\updater.log` e `result.json` |
| Chave privada de assinatura | no PC do administrador (`Documentos\MarumbiSom\chave-assinatura-atualizacoes.txt`), no pendrive de cópia e no secret `MARUMBI_SIGNING_KEY` do repositório (D29) |
| Versões publicadas | GitHub → *Releases* → **Agente 0.6.N**: pacotes assinados do agente e do Hub (o Hub baixa os dois sozinho), instalador `MarumbiSom.Agent-Setup-0.6.N.exe` |
| Pacotes do CI | GitHub → Actions → execução → artefatos `MarumbiSom.Hub-win-x64`, `MarumbiSom.Agent-win-x64`, `MarumbiSom.Agent-Setup`, `MarumbiSom.Release-win-x64` (guardados por 30 dias) |

Pastas de pacotes baixados da internet: antes de rodar scripts, no PowerShell
`Set-ExecutionPolicy -Scope Process Bypass` (vale só para aquela janela).

## Loja desconectada no painel

1. Confira no cartão da loja há quanto tempo ela não manda sinal. Se a loja diz que a música toca, não há pressa.
2. Peça para alguém da loja conferir se a internet funciona (abrir um site no PC).
3. No console do Tailscale (Machines), veja se o PC da loja aparece conectado. Se aparecer "Expired", desative a
   expiração (**⋯ → Disable key expiry**) e faça login de novo no Tailscale do PC (Área de Trabalho Remota ou
   presencial).
4. Se o PC está ligado e com internet mas o agente não conecta: Área de Trabalho Remota (abaixo) → Configurar Som →
   Status e Últimos registros.

## Caixa (Bluetooth) vermelha por mais de alguns minutos

1. Painel → Loja → Ao vivo → **Reconectar caixa**. O agente já tenta sozinho (seção 9); o botão força uma tentativa.
2. Peça para alguém da loja conferir se a caixa está ligada na tomada e ligada no botão.
3. Caixa ligada e ainda vermelha: Loja → Ao vivo → **Reiniciar áudio** (D37), que reinicia o Bluetooth e o áudio do
   Windows sem reiniciar o PC.
4. Ainda vermelha: Área de Trabalho Remota → Configurações do Windows → Bluetooth: a caixa aparece? Se não aparece
   nenhum dispositivo, o adaptador USB pode ter saído do lugar (peça para tirarem e colocarem de volta na mesma porta).
5. Último caso: pedir para desligarem e religarem a caixa, ou reiniciar o PC (Loja → Ajustes → **Reiniciar agente**
   antes de reiniciar o PC inteiro).

## Caixa verde, painel dizendo "tocando", mas a loja sem som

0. Primeiro, de onde estiver: painel → Loja → **Ao vivo** → **Reiniciar áudio** (D37). O PC reinicia o áudio e o
   Bluetooth do Windows e reconecta a caixa sem reiniciar o PC; a música para por uns 30 s e volta do mesmo ponto. A
   linha "Caixa de som" mostra "Reiniciando o áudio…" até a caixa voltar. Resolveu: anote no histórico do piloto.
   Continuou mudo: siga os passos abaixo e, no fim, reinicie o PC da loja.
1. No PC da loja (ou pela Área de Trabalho Remota): atalho **Configurar Som** → **Tocar som de teste**. Se em
   **Últimos registros** aparece "Som de teste terminou: Played" e ninguém ouviu, o agente está mandando o som para a
   caixa e o problema está entre o Windows e a caixa.
2. Confirme fora do agente: Área de Trabalho Remota → `mmsys.cpl` → aba **Reprodução** → botão direito na saída da caixa
   (ex.: "Fones de ouvido" com o nome da caixa) → **Testar**. Mudo também: não é o agente.
3. Na loja: volume da própria caixa e o botão de modo (BT/FM/USB/AUX); nenhum celular conectado nela. Depois desligar e
   ligar a caixa e deixar o agente reconectar (até 1 min). Ainda mudo: **Configurar Som** → remover a caixa, parear de
   novo e repetir o **Testar**.
4. Se o **Testar** do Windows sai e o agente continua mudo: Loja → Ajustes → **Reiniciar agente** e mande o log do dia
   (`agent-AAAAMMDD.log`) para o desenvolvimento.

## Música nova ou playlist não chega na loja

1. Painel → Início: o cartão mostra "Baixando x/y" ou "Sincronizado". O download respeita o limite de 2 Mbps; muitas
   músicas novas levam alguns minutos.
2. Loja → Eventos: procure erros de download. Arquivo que falha é tentado de novo em 10 minutos.
3. Loja desconectada: veja o procedimento acima. A troca só acontece quando 100% dos arquivos chegaram, sempre no
   fim da música que está tocando.

## Atualizar o agente das lojas

As versões saem do GitHub (D29): ao juntar algo no `main`, o GitHub Actions monta o agente, assina com a chave
guardada no secret do repositório e publica uma Release **Agente 0.6.N**. O Hub procura versão nova a cada 6 horas,
baixa sozinho e mostra **"Versão X do agente disponível"** no Início. Nenhuma loja atualiza sem você mandar.

### Uma vez: gerar a chave de assinatura

1. Baixe o artefato **MarumbiSom.Release-win-x64** do CI e extraia (ex.: `C:\MarumbiSom-release`).
2. PowerShell (sem administrador): `cd C:\MarumbiSom-release` e `.\new-signing-key.ps1`.
3. Copie `Documentos\MarumbiSom\chave-assinatura-atualizacoes.txt` para um pendrive guardado em local seguro.
   Nunca coloque a chave privada no Git, no Hub, em e-mail ou em nuvem (o secret do GitHub, abaixo, é a exceção).
4. A chave pública aparece na tela. Coloque-a em `src/MarumbiSom.Agent/release-public-key.txt` no repositório
   (ou mande para quem cuida do repositório). Só os agentes instalados a partir daí aceitam atualização remota: as
   lojas instaladas antes precisam de uma reinstalação com o instalador novo (procedimento "PC da loja formatado",
   sem desvincular).

### Uma vez: chave no GitHub e token no Hub

1. **Secret com a chave** (para o GitHub assinar): no repositório `marumbi-som`, **Settings → Secrets and variables →
   Actions → New repository secret**. Name: `MARUMBI_SIGNING_KEY`. Em *Secret*, cole a linha que começa com
   `marumbi-som-ed25519-private:` do arquivo da chave (abra no Bloco de Notas; colar o arquivo inteiro também
   funciona). **Add secret**. O GitHub não mostra o secret de novo; a cópia no pendrive continua sendo o backup.
   Quem tiver permissão de escrita no repositório consegue publicar versões assinadas: deixe o repositório só com
   você e com a verificação em duas etapas ligada.
2. **Token de leitura** (opcional desde que o repositório ficou público, D33; só é preciso se ele voltar a ser
   privado): no GitHub, foto do perfil →
   **Settings → Developer settings → Personal access tokens → Fine-grained tokens → Generate new token**. Nome
   "Hub Marumbi Som", validade a maior disponível, *Repository access*: **Only select repositories** →
   `marumbi-som`, *Repository permissions*: **Contents: Read-only**. **Generate token** e copie.
3. Painel → Configurações → Versão do agente para distribuição → **Cadastrar token** → cole → **Salvar token**. O
   Hub guarda o token cifrado e já procura a versão mais nova (a barra mostra o download, uns 110 MB).

Quando o token vencer, a tela mostra "O GitHub recusou o token": gere outro (passo 2) e use **Trocar token**. Se o
Hub for restaurado sem a pasta `data\keys`, o token salvo não abre mais: cadastre de novo.

### A cada versão

1. Junte as mudanças no `main` (ou, para publicar antes, no GitHub: **Actions → CI → Run workflow**, escolha o branch
   e marque **Publicar versão do agente**). Em uns 20 minutos a Release **Agente 0.6.N** aparece em *Releases*.
   O botão **Run workflow** só aparece depois que o `ci.yml` estiver no `main` (quando os PRs das etapas forem
   juntados). Até lá, publicar de um branch só pela API: `gh workflow run ci.yml --ref <branch> -f publicar=true`
   (ou peça a quem cuida do repositório).
2. Em até 6 horas o Hub baixa sozinho (ou Configurações → **Procurar agora**). A versão baixada vira a de distribuição
   e o Início mostra "Versão 0.6.N do agente disponível".
3. **Loja piloto primeiro**: Loja → Ajustes → **Atualizar agente**. O cartão mostra a barra do download; ao instalar,
   a música para por alguns segundos. Resultado esperado: "Agente atualizado para X".
4. Espere 1 dia com a loja piloto na versão nova.
5. Configurações → **Atualizar lojas** atualiza todas as lojas conectadas que estão numa versão anterior.
6. Loja que estava desconectada fica para depois: quando ela voltar, use **Atualizar agente** nela.

Se aparecer **"Versão X revertida"**: a versão nova não subiu em 2 minutos e a loja voltou sozinha para a anterior.
Não insista; em Configurações escolha a versão anterior em **Usar esta** e leve o `updater.log` e o log da loja para
quem cuida do código. **"Atualização para X falhou"** mostra o motivo (assinatura, espaço em disco, download); a loja
segue na versão anterior. Uma versão removida em Configurações não é baixada de novo do GitHub; a próxima, sim.

### Sem o GitHub (pacotes de teste ou GitHub fora do ar)

1. Baixe o artefato **MarumbiSom.Agent-win-x64** da execução do CI que você quer distribuir e extraia.
2. Gere o pacote assinado no seu PC:

```powershell
cd C:\MarumbiSom-release
.\sign-release.ps1 -AgentDir C:\Downloads\MarumbiSom.Agent-win-x64 -OutDir C:\MarumbiSom-pacotes
```

3. Painel → Configurações → Versão do agente para distribuição → **Enviar arquivo…** → escolha o `.zip` e o
   `.sig.json` juntos. A versão enviada vira a de distribuição; daí em diante, como acima a partir do passo 3.

### Perdi a chave privada (ou ela vazou)

1. Gere uma chave nova (`new-signing-key.ps1 -KeyFile <outro arquivo>`), troque a chave pública no repositório e o
   secret `MARUMBI_SIGNING_KEY` no GitHub. Se a chave vazou, apague o secret antigo na hora.
2. Se ainda tem a chave antiga (troca planejada): gere o pacote do build novo com a chave **antiga** e
   `-AllowKeyChange`, pelo caminho "Sem o GitHub" (o CI recusa assinar quando a chave do secret não é a do build); as
   lojas atualizadas passam a aceitar só a chave nova.
3. Sem a chave antiga: cada loja precisa ser reinstalada com o instalador do build novo (procedimento "PC da loja
   formatado", sem desvincular). Até lá as lojas seguem tocando, só não aceitam atualização remota.

## Atualizar o Hub

O Hub se atualiza sozinho (D32): a Release do GitHub traz o pacote do Hub assinado, o Hub baixa junto com o do agente
e, com **Atualizar sozinho de madrugada** ligado (padrão), instala às 03:00. Para instalar antes: Configurações →
**Atualização do Hub** → **Buscar atualizações** (procura no GitHub e já instala; com versão já baixada, o botão é
**Atualizar o Hub agora**). O painel sai do ar por cerca de 1 minuto e volta pedindo para
recarregar a página; as lojas continuam tocando e reconectam sozinhas. Os dados ficam, e o banco é copiado antes da
troca. Detalhes em [atualizacao-do-hub.md](atualizacao-do-hub.md).

Se a versão nova não subir em 2 minutos, a tarefa volta sozinha para a anterior, com o banco como estava, e o cartão
mostra "a versão X não subiu e o Hub voltou sozinho". Ela não é tentada de novo às 03:00; leve o
`updater\hub-updater.log` e o log do Hub do dia para quem cuida do código. "A atualização … não foi feita" mostra o
motivo (assinatura, pacote, serviço); o Hub segue na versão anterior.

Se o cartão avisar que **o atualizador ainda não pegou o pedido**, a tarefa não existe ou está desligada: confira
**MarumbiSom Hub - atualizador** no Agendador de Tarefas e, se faltar, instale à mão (abaixo).

### Instalar à mão (primeira vez, Hub sem a tarefa ou sem o GitHub)

1. Baixe o `MarumbiSom.Hub-<versão>.zip` da Release mais nova no GitHub (*Releases*, no repositório) ou o artefato
   **MarumbiSom.Hub-win-x64** de uma execução do CI, e extraia (ex.: `C:\MarumbiSom-hub-pacote`).
2. PowerShell **como administrador**:

```powershell
Set-ExecutionPolicy -Scope Process Bypass
cd C:\MarumbiSom-hub-pacote
.\scripts\install-hub.ps1 -SourceDir . -TailscaleServe
```

O serviço para por alguns segundos; as lojas continuam tocando e reconectam sozinhas. Os dados ficam. O script
também cria (ou recria) a tarefa do atualizador e cancela uma atualização pedida pelo painel que ainda não rodou. A
versão nova aparece em Configurações → Versão do Hub.

Sem o GitHub, também dá para gerar o pacote assinado do Hub (`sign-release.ps1 -HubDir <pasta do artefato>`, como
o do agente em "Sem o GitHub") e enviar em Configurações → Atualização do Hub → **Enviar pacote…**.

## Trocar a caixa de som

1. Pareie a caixa nova no PC da loja: Configurações do Windows → Bluetooth e dispositivos → Adicionar dispositivo
   (presencial ou pela Área de Trabalho Remota com alguém ligando a caixa em modo de pareamento).
2. Atalho **Configurar Som** → Caixa Bluetooth → escolha a nova → **Usar a caixa selecionada** → **Tocar som de teste**.
3. Remova a caixa antiga do Windows. Ajuste e marque o volume físico da caixa nova.

## PC da loja formatado ou trocado

1. Siga o [checklist de instalação](checklist-instalacao-loja.md) inteiro no PC novo (pule "Lojas → Nova loja": a
   loja já existe no painel).
2. Gere um **código de vinculação novo** na loja (Loja → Ajustes). Vincular o PC novo revoga o token do antigo.
3. O PC novo baixa as músicas e começa a tocar quando tiver tudo; a agenda, as playlists e as ofertas continuam as
   mesmas.

## PC da loja perdido ou roubado

Painel → Loja → Ajustes → **Desvincular o PC**. O token é revogado na hora e o PC não baixa mais nada do Hub. No
console do Tailscale, remova o dispositivo (**⋯ → Remove**).

## Esqueci a senha

- **Painel**: no PC do escritório, PowerShell como administrador, na pasta do pacote do Hub:
  `.\scripts\install-hub.ps1 -SourceDir . -ResetAdminPassword`.
- **Configurar Som** (loja): copie para o PC da loja o artefato **MarumbiSom.Agent-win-x64** da versão instalada,
  extraia e, no PowerShell como administrador, na pasta extraída: `.\scripts\install-agent.ps1 -SourceDir .
  -ResetLocalPassword`.
- 5 senhas erradas na Configurar Som bloqueiam por 15 minutos; o painel aceita 5 tentativas por minuto.

## Área de Trabalho Remota numa loja

Pelo Tailscale, só dos dispositivos do administrador (seção 5.2). No PC do escritório: **Conexão de Área de Trabalho
Remota** → nome Tailscale do PC da loja → conta de administrador da loja. O PC da loja precisa ter a Área de Trabalho
Remota ativada (Windows Pro: Configurações → Sistema → Área de Trabalho Remota). Ao terminar, **saia** da sessão
(não só feche a janela).

## Cópia do backup para fora do PC do escritório (configurar uma vez)

O backup diário fica no próprio PC do Hub; se o disco morrer, ele vai junto. Copie a pasta `data` para uma pasta
sincronizada com a nuvem:

1. Instale o **Google Drive para computador**, entre com a conta da empresa e, em Preferências → Meu Drive, escolha
   **Espelhar arquivos** (a pasta fica em `C:\Users\<você>\Meu Drive`).
2. PowerShell **como administrador** (troque o caminho do destino):

```powershell
$destino = 'C:\Users\<você>\Meu Drive\MarumbiSom-backup'
$copia = New-ScheduledTaskAction -Execute 'robocopy.exe' -Argument "`"C:\MarumbiSom\Hub\data`" `"$destino`" /MIR /XD work incoming keys updates /XF marumbi.db marumbi.db-wal marumbi.db-shm /R:2 /W:5 /NP /LOG:C:\MarumbiSom\Hub\logs\copia-backup.log"
Register-ScheduledTask -TaskName 'MarumbiSom - copia do backup' -Action $copia -Trigger (New-ScheduledTaskTrigger -Daily -At 3am) -User 'SYSTEM' -RunLevel Highest
Start-ScheduledTask -TaskName 'MarumbiSom - copia do backup'
```

3. Confira que `MarumbiSom-backup\backups` aparece no Drive. A cópia leva os backups do banco, a biblioteca, os
   originais e as versões do agente; o banco em uso, as chaves de sessão do painel e os pacotes e cópias da atualização
   do Hub (`updates`) ficam de fora. Se a tarefa já existe, rode de novo o `Register-ScheduledTask` acima com `-Force`
   para tirar a pasta `updates` da cópia.

## Restaurar o Hub (PC do escritório perdido ou banco estragado)

1. Instale o Hub no PC novo (Tailscale, depois `install-hub.ps1 -SourceDir . -TailscaleServe`). Use o **mesmo nome
   de máquina** no Tailscale do PC antigo (remova o antigo do console antes), para as lojas acharem o Hub no mesmo
   endereço.
2. Pare o serviço: `Stop-Service MarumbiSom.Hub`.
3. Copie o conteúdo da cópia do backup (`MarumbiSom-backup`) para `C:\MarumbiSom\Hub\data`.
4. Copie o backup mais recente `data\backups\marumbi-AAAA-MM-DD.db` para `data\marumbi.db` (substituindo).
5. Rode de novo `install-hub.ps1 -SourceDir .` (acerta as permissões da pasta) e confira o painel. Mudanças feitas
   depois das 02:00 do dia do backup se perdem; o painel pede login de novo.
6. As lojas reconectam sozinhas com o mesmo token. Se mudou o endereço do Hub, cada loja precisa de **Desvincular** e
   **Vincular esta loja** na Configurar Som com o endereço novo e um código novo.
7. O token do GitHub não vai na cópia (a pasta `keys` fica de fora): cadastre de novo em Configurações (passo
   "Token de leitura" em "Atualizar o agente das lojas").

Se só o banco estragou (o PC está bom): passos 2, 4 e `Start-Service MarumbiSom.Hub`.

## Ofertas

- Confira preço e validade de cada oferta antes de publicar. A oferta para de tocar sozinha no fim da validade,
  mesmo com a loja sem internet.
- Ofertas → coluna "Tocou (7 dias)" e a tela de cada oferta mostram quantas vezes tocou em cada loja (prova de
  veiculação).
