# Marumbi Som

Sistema de som remoto das lojas da Casa de Carnes Marumbi: música ambiente e ofertas faladas controladas do
escritório ou do celular, com cada loja tocando do cache local.

**A fonte da verdade é o [SISTEMA_SOM_MARUMBI.md](SISTEMA_SOM_MARUMBI.md).** Toda implementação segue esse
documento; qualquer desvio é registrado na seção 18 antes de ser implementado.

## Situação

| Etapa | Estado |
|---|---|
| 0 — Preparação (humano) | em andamento |
| 1 — Prova de conceito Bluetooth/áudio como serviço | música e oferta tocando como serviço no PC piloto; falta o restante do aceite ([roteiro](docs/etapa-1-prova-de-conceito.md)) |
| 2 — Agente offline completo | código pronto, aguardando teste no PC piloto ([roteiro](docs/etapa-2-agente-offline.md)) |
| 3 — Hub e sincronização | código pronto, aguardando teste no escritório e no PC piloto ([roteiro](docs/etapa-3-hub-sincronizacao.md)) |
| 4 — Painel | código pronto, aguardando teste no escritório ([roteiro](docs/etapa-4-painel.md)) |
| 5 a 8 | não iniciadas |

## Estrutura

```
src/
  MarumbiSom.Contracts/       DTOs: manifesto, mensagens WebSocket, hash canônico
  MarumbiSom.Agent.Windows/   interop: rádio, Bluetooth, KS, Core Audio, IPolicyConfig, serviços, PnP
  MarumbiSom.Agent/           serviço Windows da loja: guardião Bluetooth, player (agenda, mixagem, ofertas),
                              cache, estado salvo, sincronização com o Hub e a página Configurar Som (127.0.0.1:47800)
  MarumbiSom.Hub/             serviço Windows do escritório (127.0.0.1:5080): SQLite, ingestão com ffmpeg,
                              playlists, lojas, manifestos, API do painel e das lojas, WebSocket; serve o painel
web/panel/                    painel React (Vite, TanStack Query, Tailwind); tipos gerados do OpenAPI do Hub
tests/                        xUnit (os testes do agente sobem um Hub de verdade em memória)
scripts/                      instalação do agente e do Hub, hardening do PC, download do mpv/ffmpeg com SHA-256,
                              hub-teste.ps1 (operações de teste pela API; o painel faz o mesmo pelo navegador)
infra/tailscale-acl.hujson    política de acesso do Tailscale (seção 5.2)
docs/                         roteiros e procedimentos
```

O Updater entra na Etapa 6.

## Desenvolvimento

Requer o SDK do .NET 10.

```bash
dotnet build
dotnet test
dotnet run --project src/MarumbiSom.Agent -- --build-manifest --music /caminho/para/musicas --Agent:DataDirectory=/tmp/som
echo "senha-de-teste" | dotnet run --project src/MarumbiSom.Agent -- --set-local-password --Agent:DataDirectory=/tmp/som
dotnet run --project src/MarumbiSom.Agent -- --simulate --Agent:DataDirectory=/tmp/som   # http://127.0.0.1:47800
echo "senha-muito-forte" | dotnet run --project src/MarumbiSom.Hub -- --create-admin admin --Hub:DataDirectory=/tmp/hub
dotnet run --project src/MarumbiSom.Hub -- --Hub:DataDirectory=/tmp/hub                 # http://127.0.0.1:5080
```

Os testes de ingestão do Hub usam o ffmpeg real (precisa estar no PATH).

Painel (Node 22): `cd web/panel && npm ci && npm run dev` (http://localhost:5173, usando o Hub em
`127.0.0.1:5080`); `npm run lint`, `npm test`, `npm run build` e `npm run e2e` (ponta a ponta com Hub real e
agente simulado). Detalhes em [docs/etapa-4-painel.md](docs/etapa-4-painel.md).

Os projetos Windows compilam em qualquer sistema (`EnableWindowsTargeting`); o interop real só roda no Windows.
O modo `--simulate` troca Bluetooth, endpoints de áudio e mpv por implementações falsas.

Publicar para os PCs (o CI faz isso e anexa os pacotes `MarumbiSom.Agent-win-x64` e `MarumbiSom.Hub-win-x64`):

```powershell
dotnet publish src\MarumbiSom.Agent -c Release -r win-x64 --self-contained -o publish\agent
.\scripts\download-tools.ps1 -Destination publish\agent\tools
dotnet publish src\MarumbiSom.Hub -c Release -r win-x64 --self-contained -o publish\hub
.\scripts\download-tools.ps1 -For hub -Destination publish\hub\tools
cd web\panel; npm ci; npm run build; cd ..\..
Copy-Item -Recurse web\panel\dist publish\hub\wwwroot
```

## Regras que o código não pode quebrar

Ver seção 2 do documento. As mais relevantes para quem mexe no agente: a loja nunca depende de rede para tocar,
nunca muda volume em degrau, nunca abre porta de entrada, e segredos nunca vão para o Git.
