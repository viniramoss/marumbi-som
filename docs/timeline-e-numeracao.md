# Linha do tempo das ofertas e músicas numeradas (D30, D31)

Objetivo: posicionar cada oferta presa vendo o tempo da playlist, como num editor de vídeo (D30), e saber
exatamente qual música está tocando, já que as músicas do piloto vieram com as mesmas tags e ficaram todas com
o mesmo nome (D31).

## O que esta versão faz

| Onde | O que mudou |
|---|---|
| **Biblioteca** | Toda música enviada recebe o próximo número como título (`001`, `002`, …), na ordem em que o Hub termina de processar. A lista fica na ordem do número, com a coluna **Nº** e o nome do arquivo enviado em letra menor embaixo do título. A busca acha também pelo nome do arquivo. O lápis (**Renomear**) troca o título e o artista quando quiser |
| **Músicas que já estavam no Hub** | Na primeira vez que o Hub novo inicia, elas ganham número pela ordem em que foram importadas. As que já tinham sido renomeadas no painel ganham o número mas mantêm o nome |
| **Áudios de oferta** | Não mudam: continuam com o nome da oferta e sem número |
| **Playlists → editor** | **Linha do tempo** acima das colunas: a playlist inteira do começo ao fim, como num editor de vídeo (ex.: 37 min de ponta a ponta), com a faixa de ofertas em cima e a de músicas embaixo. Abre ajustada à largura da tela; as lupas, ou Ctrl + rolar o mouse, aproximam, e o botão de expandir volta para a playlist inteira. A régua mostra o tempo da playlist e uma guia acompanha o mouse. Cada oferta presa é um rótulo amarelo no ponto em que entra, com o nome e o horário na playlist, e uma faixa amarela por cima da música mostra quanto ela dura. Arraste uma oferta da coluna **Ofertas** até o ponto exato (o rótulo "22:17 · 007 aos 0:40" mostra onde vai cair; perto do começo de uma música ela encaixa no 0:00); arraste o rótulo para mudar; clique para o ajuste fino ou para soltar; com o rótulo selecionado, as setas movem 1 s (Shift: 10 s). Continua valendo soltar a oferta em cima da música na lista |
| **Música tocando** | No editor, a música que cada loja está tocando fica marcada na lista ("Tocando agora em Loja X 1:20 de 3:40") e na linha do tempo, com um marcador do nome da loja andando junto. No Início e em Loja → Ao vivo o cartão mostra o número (ou o nome) e a barra de andamento |

Nada muda no agente: a linha do tempo grava a mesma âncora "durante a música" da Etapa 5 (a música daquele ponto +
"entra aos"), sempre até 5 s antes do fim da música e arredondada para o segundo. Por isso, reordenar as músicas leva
a oferta junto com a música, e a mixagem entre músicas não atrasa a oferta. Com o aleatório ligado a linha mostra só a
ordem da lista, sem ofertas presas.

O número nunca é reaproveitado: excluir a música 007 não faz a próxima virar 007. Enviar de novo um arquivo
igual (mesmo conteúdo) traz de volta a mesma música, com o mesmo número.

## Instalar

Basta atualizar o Hub (artefato **MarumbiSom.Hub-win-x64** do CI do PR, como na [Etapa 4](etapa-4-painel.md)).
A numeração das músicas antigas acontece sozinha na primeira inicialização; faça antes o backup de costume
(`data\` inteira), porque a troca de nome não tem volta automática. O agente não precisa ser atualizado.

## Critérios de aceite

| # | Teste | Como fazer | Meta | Resultado |
|---|---|---|---|---|
| 1 | Numeração das antigas | Atualize o Hub e abra a Biblioteca | Todas as músicas têm número na ordem em que foram importadas, com o nome do arquivo embaixo; as renomeadas antes mantêm o nome | |
| 2 | Música nova | Envie duas músicas | Entram com os dois próximos números; o nome do arquivo aparece em letra menor | |
| 3 | Número não volta | Remova a música mais nova (fora de playlist) e envie outra diferente | A nova pega o número seguinte, não o da removida | |
| 4 | Renomear | Lápis numa música, troque o título e salve | O nome novo aparece na Biblioteca, no editor de playlist, no Início e em Ao vivo quando ela tocar | |
| 5 | Soltar na linha do tempo | No editor de uma playlist sequencial, arraste uma oferta para o meio de uma música na linha do tempo e salve | O bloco aparece ali, a lista mostra "aos m:ss" na música certa e a loja toca a oferta nesse ponto (±2 s), por cima da música | |
| 6 | Mover a oferta | Arraste o rótulo para outro ponto (outra música); depois clique nele e use as setas | Muda a música e o segundo; nunca passa de 5 s antes do fim da música | |
| 7 | Playlist inteira e zoom | Abra uma playlist longa; use as lupas e Ctrl + rolar o mouse; depois o botão de expandir | Abre com a playlist toda na tela; o zoom aproxima sem perder o ponto e o expandir volta para a playlist inteira | |
| 8 | Tocando agora | Com a loja tocando essa playlist, abra o editor | A música da loja fica marcada na lista e o marcador com o nome da loja anda na linha do tempo | |
| 9 | Testes automatizados | CI do PR | Migração da numeração, ingestão numerando, linha do tempo (soltar, arrastar, setas, tocando agora) e o teste ponta a ponta do painel | ✅ |
