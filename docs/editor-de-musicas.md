# Editor de música (D34)

Objetivo: tirar um trecho de uma música (ou cortar o começo e o fim) pelo painel, sem outro programa. Pedido do
proprietário em 26/10/2026.

## Como usar

1. **Biblioteca** → na linha da música, o botão da tesoura (**Editar trechos**). A tela baixa a música do Hub (com
   barra de andamento) e desenha a onda.
2. Marque o trecho que vai sair de um destes jeitos:
   - arrastando na onda;
   - ouvindo: **Marcar início** no começo do trecho e **Marcar fim** no fim (atalhos: <kbd>I</kbd> e <kbd>O</kbd>;
     espaço toca e pausa);
   - digitando o início e o fim na lista de trechos (ex.: `1:05,2`).

   A borda de um trecho ajusta arrastando. Dá para marcar vários trechos; o zoom (até 16×) ajuda a acertar o ponto.
3. Confira ouvindo: **Ouvir o trecho** toca só o que vai sair; **Ouvir como fica** toca 3 s antes e 3 s depois da
   emenda, já pulando o trecho. Com **Pular os trechos marcados ao ouvir** ligado, o **Ouvir** toca a música como ela
   vai ficar.
4. **Salvar sem os trechos**. A barra mostra o Hub refazendo a música; ao terminar, a tela abre a música editada.

A música continua com o mesmo número, título e playlists. Na Biblioteca ela aparece com a etiqueta **editada**. As
lojas recebem a versão nova na próxima sincronização, sem parar de tocar.

## O que o Hub faz

| Passo | O quê |
|---|---|
| Arquivo de partida | O arquivo enviado guardado em `data\originals\` (sem perda). Sem ele (biblioteca restaurada de um backup antigo), o da biblioteca |
| Processamento | O mesmo da ingestão (seção 6.5): corta o silêncio das pontas, tira os trechos com 20 ms de fade em cada emenda (para não estalar), faz os fades, iguala o volume e converte para AAC |
| Troca | O resultado é um arquivo novo (SHA-256 novo). A música nova herda número, título, artista e data; entra nas playlists no lugar da anterior; a anterior fica excluída logicamente, sem número |
| Ofertas | As âncoras "durante" presas na música andam junto: um ponto depois do trecho tirado volta o tamanho do trecho; um ponto dentro do trecho vai para a emenda |
| Lojas | Os manifestos são gerados de novo; cada loja baixa o arquivo novo na próxima sincronização |

A edição entra na mesma fila dos envios (uma de cada vez) e sobrevive a um reinício do Hub.

## Editar de novo e voltar para a original

A música guarda de qual arquivo enviado veio (`source_hash`) e os trechos tirados no tempo dele (`cuts_json`). Editar
de novo soma os trechos novos aos de antes, sempre a partir do arquivo enviado (sem acumular perda). **Voltar para a
original** refaz a música sem nenhum corte: sai exatamente o arquivo de antes, que volta com o número.

Enviar de novo o arquivo de uma música editada aparece como "Já estava na biblioteca" (para desfazer, use **Voltar
para a original**).

## Limites

- A música editada precisa continuar com pelo menos 30 s.
- Nesta versão só músicas; o áudio de oferta continua sendo trocado na própria oferta.
- A onda é calculada no navegador a partir do `.m4a` (AAC): Chrome e Edge abrem normalmente. O Chromium do Playwright
  não tem AAC, por isso o teste ponta a ponta entrega um WAV com o mesmo tom.

## Critérios de aceite

| # | Teste | Como fazer | Meta | Resultado |
|---|---|---|---|---|
| 1 | Tirar um trecho | Numa música que está numa playlist com uma oferta presa, marque um trecho e salve | A barra anda até o fim; a tela abre a editada com a duração menor; na Biblioteca, mesmo número, etiqueta **editada** e a mesma playlist | |
| 2 | Lojas | Depois do teste 1, acompanhe a loja | A loja baixa a versão nova e continua tocando; quando chegar a vez dela, toca sem o trecho e sem estalo na emenda | |
| 3 | Oferta presa | Antes do teste 1, prenda a oferta depois do trecho que vai sair | A oferta continua no mesmo ponto da música (na timeline, andou o tamanho do trecho) | |
| 4 | Voltar | **Voltar para a original** | A música volta inteira, sem a etiqueta | |
| 5 | Testes automatizados | CI do PR | Conta dos trechos, corte com ffmpeg de verdade, troca nas playlists e nas âncoras, editar de novo, voltar para a original, reenvio do original e a tela do editor (unitário e ponta a ponta) | ✅ |

## Onde olhar

Log do Hub (`logs\hub-AAAAMMDD.log`): `Edição de 019 na fila: N trecho(s) tirado(s) do original` e
`019 editada: <hash antigo> → <hash novo>, X s tirados do original, N item(ns) de playlist e N âncora(s) ajustados`.
O pedido também fica na auditoria (`tracks.edit`).
