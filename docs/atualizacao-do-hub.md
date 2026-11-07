# Atualização automática do Hub (D32)

Objetivo: o Hub se atualizar sozinho como o agente das lojas, sem precisar baixar o pacote e rodar o
`install-hub.ps1` a cada versão. Pedido do proprietário em 26/10/2026.

## O que esta versão faz

| Onde | O que mudou |
|---|---|
| **GitHub (CI)** | A Release passa a levar o pacote do Hub assinado (`MarumbiSom.Hub-<versão>.zip` e `.sig.json`), com a mesma chave do agente. A assinatura do Hub é própria (`marumbi-som-hub\|versão\|sha256`): a assinatura de um pacote do agente não serve para o Hub |
| **Hub** | Procura o pacote do Hub junto com o do agente (a cada 6 horas ou em **Procurar agora**), baixa com barra de andamento e confere SHA-256 e assinatura. Às 03:00 (hora do escritório), com a chave ligada, pede a troca para a versão baixada, uma vez por versão |
| **Configurações → Atualização do Hub** | Versão instalada e disponível, **Atualizar o Hub agora**, a chave **Atualizar sozinho de madrugada** (ligada por padrão), o andamento e o resultado da última atualização. **Enviar pacote…** aceita o `.zip` e o `.sig.json` gerados pelo `sign-release.ps1 -HubDir` (testes ou GitHub fora do ar) |
| **Windows** | O `install-hub.ps1` cria a tarefa agendada **MarumbiSom Hub - atualizador** (como SYSTEM, a cada minuto e na inicialização). É ela que para o serviço, troca os arquivos e inicia a versão nova; sem pedido, sai na hora |
| **Painel aberto** | Quando o Hub volta numa versão diferente, aparece **"O Hub foi atualizado para a versão X"** com o botão **Recarregar** |

As lojas não param de tocar: o agente funciona sem o Hub (seção 8). Durante a troca (cerca de 1 minuto) o painel
fica fora do ar e os comandos para as lojas esperam.

## Como a troca funciona

1. O Hub grava o pedido em `data\updates\pending.json` (botão ou 03:00). Ele não troca os próprios arquivos: roda
   com a conta do serviço, que só lê `app\`.
2. Em até 1 minuto a tarefa roda o `updater\MarumbiSom.Updater.exe --hub`. Ele lê o pedido sem seguir junções nem
   links, copia o pacote para `updater\work`, confere de novo SHA-256 e assinatura, recusa versão igual ou mais antiga
   que a instalada e confere que o `.dll` do pacote é da versão assinada. Até aqui o Hub segue no ar: um pacote ruim é
   recusado sem derrubar nada.
3. Para o serviço (e ffmpeg/ffprobe que tenham sobrado), guarda `app\` em `updater\work\backup`, troca os arquivos
   (o `appsettings.Production.json` fica) e inicia.
4. O Hub novo, antes de abrir o banco, copia `marumbi.db` para `data\updates\banco-antes\<pedido>` e, depois de subir,
   grava `data\updates\confirmed.json`.
5. Sem a confirmação em 2 minutos, o atualizador devolve `app\`; o Hub antigo, ao subir, devolve o banco da cópia.
   Uma troca interrompida por queda de energia é desfeita na execução seguinte da tarefa (na inicialização do PC).
6. O resultado fica em `updater\result.json`; o Hub registra no histórico de eventos (`hub.update.done`,
   `hub.update.rolledBack` ou `hub.update.failed`) e mostra no cartão. Ficam as 3 últimas cópias do banco.

O SYSTEM nunca grava em `data\` (a cópia e a volta do banco são do próprio Hub), e o serviço só lê `app\` e
`updater\`. Assim, um Hub invadido não consegue usar a tarefa para virar administrador do PC.

Uma versão que voltou ou falhou não é tentada de novo sozinha às 03:00; o botão continua oferecendo. O pedido que o
atualizador não pegar em 30 minutos é desfeito (o cartão avisa depois de 3 minutos para conferir a tarefa).

## Instalar (uma vez)

O Hub instalado hoje não tem a tarefa: a primeira vez é à mão, como sempre. Baixe o artefato
**MarumbiSom.Hub-win-x64** do CI do PR, extraia (ex.: `C:\MarumbiSom-hub-pacote`) e, no PowerShell **como
administrador**:

```powershell
Set-ExecutionPolicy -Scope Process Bypass
cd C:\MarumbiSom-hub-pacote
.\scripts\install-hub.ps1 -SourceDir .
```

O final mostra "Versões novas do Hub: instaladas sozinhas às 03:00 …". Confira no Agendador de Tarefas do Windows a
tarefa **MarumbiSom Hub - atualizador** e, no painel, Configurações → **Atualização do Hub** sem o aviso "O
atualizador ainda não está instalado". Daí em diante, as versões publicadas no GitHub se instalam sozinhas.

O `install-hub.ps1` continua servindo para instalar à mão: ele cancela uma atualização pedida ou pela metade e
recria a tarefa.

## Pacotes de teste

Sem publicar no GitHub, gere o pacote assinado de um build mais novo que o instalado (artefato
**MarumbiSom.Hub-win-x64** de outra execução do CI) com a ferramenta do artefato **MarumbiSom.Release-win-x64**:

```powershell
cd C:\MarumbiSom-release
.\sign-release.ps1 -HubDir C:\Downloads\MarumbiSom.Hub-win-x64 -OutDir C:\MarumbiSom-pacotes
# Assinatura inválida (o Hub recusa no envio):
.\sign-release.ps1 -HubDir C:\Downloads\MarumbiSom.Hub-win-x64 -OutDir C:\MarumbiSom-pacotes -TestInvalidSignature -Version 0.6.998
# Hub que não sobe (o atualizador volta sozinho, com o banco de antes):
.\sign-release.ps1 -HubDir C:\Downloads\MarumbiSom.Hub-win-x64 -OutDir C:\MarumbiSom-pacotes -TestBroken
```

O pacote quebrado é o build de verdade com um endereço inválido no `appsettings.json`: o Hub abre o banco (e guarda
a cópia), não consegue escutar e cai antes de confirmar. Por isso ele usa a versão do build, que precisa ser mais
nova que a instalada.

## Critérios de aceite

| # | Teste | Como fazer | Meta | Resultado |
|---|---|---|---|---|
| 1 | Tarefa instalada | Instale uma vez à mão (acima) | Tarefa no Agendador; cartão sem o aviso do atualizador; chave da madrugada ligada | |
| 2 | Versão quebrada | Com o build B: `-TestBroken`, **Enviar pacote…** e **Atualizar o Hub agora** (as lojas seguem tocando) | Em até ~4 min o painel volta na versão anterior e o cartão mostra "a versão B não subiu e o Hub voltou sozinho … com o banco como estava" | |
| 3 | Atualizar agora | Com o mesmo build B, sem `-TestBroken`: envie e **Atualizar o Hub agora** | O painel sai do ar por ~1 min, volta com **Recarregar**; "Atualizado da A para a B"; as lojas não param | |
| 4 | Madrugada | Com um build C mais novo: envie e deixe a chave ligada até o dia seguinte | Às 03:00 o Hub se atualiza sozinho; de manhã, "Atualizado da B para a C …, de madrugada" | |
| 5 | Assinatura inválida | `-TestInvalidSignature -Version 0.6.998` e **Enviar pacote…** | Recusado no envio ("Assinatura inválida…"); o Hub não para | |
| 6 | Pelo GitHub | Depois de juntar no `main` (ou Run workflow com **Publicar**) | A versão aparece como disponível sozinha, com a barra do download | |
| 7 | Testes automatizados | CI do PR | Assinatura própria do Hub, recusa de pacote adulterado, de versão antiga e de pacote incompleto, atualizador (troca, volta, queda no meio, pedido repetido, caminhos com link), cópia e volta do banco, madrugada uma vez por versão, procura no GitHub e o cartão do painel | ✅ |

A ordem 2 → 3 → 4 usa só três builds (A instalado, B, C): o pacote quebrado e o bom do mesmo build têm a mesma
versão, e a versão que voltou não é tentada às 03:00, por isso a madrugada fica com o build C.

## Onde olhar

- Log do atualizador: `C:\MarumbiSom\Hub\updater\hub-updater.log` (`Atualizando o Hub de A para B`, `Versão B
  confirmada pelo Hub` ou `… Restaurando a versão A`).
- Log do Hub: `C:\MarumbiSom\Hub\logs\hub-AAAAMMDD.log` (`Versão B do Hub subindo pelo atualizador; cópia do banco
  guardada`, `Hub atualizado de A para B` ou `… voltou para a A; banco devolvido para a cópia de antes da
  atualização`).
- Cópias do banco: `C:\MarumbiSom\Hub\data\updates\banco-antes`.
