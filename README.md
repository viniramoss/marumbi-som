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
| 3 — Hub e sincronização | em desenvolvimento, em paralelo ao aceite das etapas 1 e 2 (D18) |
| 4 a 8 | não iniciadas |

## Estrutura

```
src/
  MarumbiSom.Contracts/       DTOs: manifesto, mensagens WebSocket, hash canônico
  MarumbiSom.Agent.Windows/   interop: rádio, Bluetooth, KS, Core Audio, IPolicyConfig, serviços, PnP
  MarumbiSom.Agent/           serviço Windows da loja: guardião Bluetooth, player (agenda, mixagem, ofertas),
                              cache, estado salvo e a página Configurar Som (127.0.0.1:47800)
tests/                        xUnit
scripts/                      instalação, hardening do PC, download do mpv com SHA-256
infra/tailscale-acl.hujson    política de acesso do Tailscale (seção 5.2)
docs/                         roteiros e procedimentos
```

O Hub, o painel e o Updater entram nas etapas 3 a 6.

## Desenvolvimento

Requer o SDK do .NET 10.

```bash
dotnet build
dotnet test
dotnet run --project src/MarumbiSom.Agent -- --build-manifest --music /caminho/para/musicas --Agent:DataDirectory=/tmp/som
echo "senha-de-teste" | dotnet run --project src/MarumbiSom.Agent -- --set-local-password --Agent:DataDirectory=/tmp/som
dotnet run --project src/MarumbiSom.Agent -- --simulate --Agent:DataDirectory=/tmp/som   # http://127.0.0.1:47800
```

Os projetos Windows compilam em qualquer sistema (`EnableWindowsTargeting`); o interop real só roda no Windows.
O modo `--simulate` troca Bluetooth, endpoints de áudio e mpv por implementações falsas.

Publicar o agente para o PC da loja:

```powershell
dotnet publish src\MarumbiSom.Agent -c Release -r win-x64 --self-contained -o publish\agent
```

## Regras que o código não pode quebrar

Ver seção 2 do documento. As mais relevantes para quem mexe no agente: a loja nunca depende de rede para tocar,
nunca muda volume em degrau, nunca abre porta de entrada, e segredos nunca vão para o Git.
