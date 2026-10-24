# Etapa 5 — Ofertas

Objetivo (seção 15): cadastrar as ofertas faladas no painel, prender ofertas em músicas da playlist e criar
regras de horário, com a música abaixando por baixo da locução (ducking) e o registro de cada execução.

Pela D24, toda oferta criada pelo painel toca **por cima da música**, com a música abaixada. O painel não
oferece o modo "entre músicas" (a oferta tocando sozinha, com a música parada).

## O que esta versão faz

| Tela | O que tem |
|---|---|
| **Ofertas** | Lista com situação (agendada, no ar, vencida, desativada), aviso de "vence em menos de 24 h", quando toca, validade, lojas e quantas vezes tocou nos últimos 7 dias; o clique em qualquer parte da linha abre a oferta |
| **Oferta → editar** | Áudio (arrastar ou escolher, com barra de envio e de processamento; o Hub corta o silêncio e iguala a locução em −14 LUFS), pré-escuta, validade (começa e termina), lojas (nenhuma marcada = todas), volume da música durante a oferta (padrão 30%), regras "a cada X minutos" e "horário fixo" (com dias da semana e restrição opcional a playlists), músicas em que está presa e execuções dos últimos 7 dias por loja (tocou, cortada, descartada) |
| **Playlists → editor** | Coluna de ofertas ao lado das músicas: arraste a oferta para cima de uma música e marque o momento em que ela entra (até 5 s antes do fim). Reordenar leva a oferta junto; tirar a música com oferta presa pede confirmação; ligar o aleatório pede confirmação e solta as ofertas presas |
| **Início** | Próxima oferta prevista e última oferta tocada em cada loja |
| **Loja → Ao vivo** | **Tocar oferta agora** (escolhe entre as ofertas que valem na loja), próxima e última oferta |
| **Loja → Histórico** | Ofertas junto com as músicas, com filtro só de ofertas |
| **Loja → Ajustes** | Intervalo mínimo entre ofertas (padrão 5 min), que vale para todas as regras da loja |

Regras de prioridade (seção 10.3.5): "tocar oferta agora" > horário fixo > ofertas presas na música > a cada X
minutos. As regras respeitam o intervalo mínimo; ofertas presas e "tocar agora" ignoram o intervalo, mas nunca
tocam por cima de outra oferta. Oferta que espera mais de 10 min na fila é descartada.

A validade é conferida pelo relógio do PC da loja logo antes de tocar, então uma oferta vencida não toca
mesmo com o Hub desligado. Fora do horário da agenda, ou com a música pausada pelo painel, as regras não
tocam (só "tocar oferta agora").

## Instalar ou atualizar

Use os artefatos **MarumbiSom.Hub-win-x64** e **MarumbiSom.Agent-win-x64** (ou o instalador
**MarumbiSom.Agent-Setup**) do CI do PR. A instalação do Hub é igual à da [Etapa 4](etapa-4-painel.md); o agente
precisa ser atualizado para mandar a próxima oferta prevista. Como esta versão já traz a Etapa 6, siga o
[checklist de instalação da loja](checklist-instalacao-loja.md) para o agente.

## Critérios de aceite (seção 15, Etapa 5)

Antes: envie duas locuções curtas (5 a 20 s) na tela da oferta e deixe a loja piloto tocando uma playlist
**sequencial** (aleatório desligado).

| # | Teste | Como fazer | Meta | Resultado |
|---|---|---|---|---|
| 1 | Nenhuma oferta com a música parada (D24, D26 i) | Crie ofertas com regra, presas em música e por "tocar oferta agora" | Todas tocam por cima da música abaixada; nenhuma toca com a música parada | |
| 2 | Oferta presa na música | No editor da playlist, prenda a oferta A numa música, entrando aos 1:00. Espere essa música tocar | A música abaixa suave para 30%, a oferta entra aos 1:00 (±2 s) e a música volta suave | |
| 3 | Reordenar | Mova a música com a oferta presa para outra posição e salve | A oferta continua presa na mesma música (ícone na coluna de ofertas) e toca quando ela tocar | |
| 4 | Regras ao longo do dia | Oferta A "a cada 15 minutos" e oferta B "horário fixo" em dois horários do dia (ex.: 11:00 e 16:30). Deixe a loja o dia todo | No fim do dia, Loja → Histórico (filtro de ofertas) mostra A a cada ~15 min e B nos dois horários (atrasos só por causa do intervalo mínimo ou de outra oferta tocando) | |
| 5 | Duas ofertas juntas | Coloque A e B em horário fixo no mesmo minuto | Não se sobrepõem: B toca depois de A respeitando o intervalo mínimo da loja (5 min), ou é descartada após 10 min na fila | |
| 6 | Oferta vencida com o Hub desligado | Crie uma oferta "a cada 5 minutos" que vence daqui a 20 min. Assim que a loja tocar a oferta uma vez, pare o serviço `MarumbiSom.Hub` | Depois do horário de vencimento ela não toca mais, mesmo sem Hub | |
| 7 | Oferta só de uma loja | Cadastre "Loja 2" no painel (não precisa de PC) e crie uma oferta "a cada 5 minutos" marcada só para a Loja 2 | A loja piloto não toca essa oferta (Histórico da loja piloto sem ela) | |
| 8 | Contagem de execuções | Ofertas → coluna "Tocou (7 dias)" e a tela da oferta | Mostram as execuções por loja, iguais ao Histórico | |
| 9 | Testes automatizados | CI do PR | SpotScheduler (prioridade, intervalo, fila de 3, descarte em 10 min, validade com relógio simulado, âncoras presas ao item, ducking 800 ms / 1.500 ms), API de ofertas do Hub e o teste ponta a ponta do painel (criar oferta, prender na playlist, tocar agora) | ✅ |

Registros úteis no log da loja: `Oferta of-N disparada por pl-N (prioridade, During)`, `Oferta '…' terminou: Played`
e `Oferta of-N (pl-N) descartada: motivo`.
