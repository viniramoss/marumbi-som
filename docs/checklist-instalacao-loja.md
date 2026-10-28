# Checklist de instalação de uma loja

Instalação do zero do som num PC de loja (seções 14.1 e 16.3). Siga na ordem e marque cada item. Leva cerca de
1 hora com a caixa já em mãos. Quem instala precisa da senha do administrador do PC, da conta do Tailscale e do
acesso ao painel.

## Antes de ir à loja (no escritório)

- [ ] O Hub está instalado e o painel abre em `https://<hub>.<tailnet>.ts.net` (veja o [runbook](runbook.md)).
- [ ] No painel, **Lojas → Nova loja**: nome (ex.: "Loja 2 - Centro"), volume padrão e fuso.
- [ ] A loja tem **Agenda** (Loja → Agenda) cobrindo o horário de funcionamento, com uma playlist que tenha
      músicas. Sem agenda a loja não toca nada.
- [ ] Baixe o instalador **MarumbiSom.Agent-Setup-<versão>.exe** da Release mais nova no GitHub (repositório →
      *Releases* → **Agente 0.6.N**) e copie para um pendrive. Sem Release ainda: Actions → execução mais recente da
      `main` → artefato **MarumbiSom.Agent-Setup**, de um build que já tenha a chave pública de atualização (veja o
      runbook).

## No PC da loja

### 1. Contas do Windows

- [ ] Existe uma conta de **administrador** com senha forte, conhecida só pelo administrador. Faça toda a
      instalação logado nela.
- [ ] O funcionário usa uma conta **padrão**. Confira no PowerShell: `net localgroup Administradores` (a conta
      do funcionário não pode aparecer na lista).

### 2. Adaptador Bluetooth e caixa

- [ ] O adaptador Bluetooth USB está **direto numa porta do PC** (de preferência atrás), sem hub USB, longe de
      metal, com a caixa a no máximo ~5 m e sem parede no caminho.
- [ ] A caixa fica sempre ligada na tomada, com o desligamento automático desativado (se ela permitir).
- [ ] O volume físico da caixa está num nível fixo e marcado ("não mexer").

### 3. Tailscale

- [ ] Instale o Tailscale (tailscale.com/download) e faça login com a conta do administrador.
- [ ] Ícone do Tailscale na bandeja → **Preferences** (ou Configurações) → marque **Run unattended**.
- [ ] No console do Tailscale (login.tailscale.com → Machines), no PC da loja: **⋯ → Edit ACL tags** → `tag:loja`.
      Na mesma linha deve aparecer **Expiry disabled** (se não, **⋯ → Disable key expiry**).
- [ ] No PC da loja, abra `https://<hub>.<tailnet>.ts.net/healthz` no navegador: deve responder `{"ok":true}`.

### 4. Parear a caixa

- [ ] Configurações do Windows → **Bluetooth e dispositivos → Adicionar dispositivo → Bluetooth** → escolha a
      caixa. Ela aparece como "Conectado" ou "Pareado".

### 5. Instalar o agente

- [ ] Rode o **MarumbiSom.Agent-Setup-<versão>.exe** (o Windows pede confirmação de administrador) → Avançar →
      **Instalar**.
- [ ] Uma janela do PowerShell abre e pede a **senha da página Configurar Som** (mínimo 8 caracteres) duas vezes.
      Guarde no gerenciador de senhas. A janela termina com "Instalado" (tecle Enter para fechar). Se aparecer
      erro em vermelho, anote a mensagem e veja o runbook.
- [ ] O atalho **Configurar Som** aparece na área de trabalho do administrador.

### 6. Preparar o PC (energia, horário, atualizações)

- [ ] Abra o **PowerShell como administrador** e rode, trocando `funcionario` pelo nome da conta do funcionário:

```powershell
Set-ExecutionPolicy -Scope Process Bypass
& "C:\Program Files\MarumbiSom\scripts\harden-store-pc.ps1" -EmployeeAccount funcionario
```

- [ ] Confira o relatório no fim: nenhum item `[ATENÇÃO]` sem explicação. Ele fica gravado em
      `C:\ProgramData\MarumbiSom\harden-report-<data>.txt`.
- [ ] Gerenciador de Dispositivos → adaptador Bluetooth e cada **Hub raiz USB** → Propriedades → Gerenciamento
      de Energia: **"Permitir que o computador desligue este dispositivo"** desmarcado.
- [ ] (Opcional) Na BIOS: "Restore on AC Power Loss" = **Power On**.

### 7. Vincular a loja e escolher a caixa

- [ ] No painel (pelo celular ou outro PC): Loja → **Ajustes → Gerar código de vinculação** (vale 30 minutos).
- [ ] No PC da loja, atalho **Configurar Som** → entre com a senha local.
- [ ] Seção **Hub (escritório)**: endereço `https://<hub>.<tailnet>.ts.net` e o código → **Vincular esta loja**.
      Aparece "Conectada ao Hub" e o andamento do download das músicas.
- [ ] Seção **Caixa Bluetooth**: escolha a caixa → **Usar a caixa selecionada** → **Tocar som de teste**: dois
      bipes saem na caixa.

### 8. Conferir no painel

- [ ] O cartão da loja no Início mostra a loja conectada, a caixa conectada e, depois do download, "Sincronizado".
- [ ] Loja → Ao vivo mostra a música tocando e a versão do agente.

## Aceite da loja (seção 16.3)

- [ ] Reinicie o PC e **não faça login**: a música começa sozinha em até ~3 minutos (o serviço
      tem início atrasado).
- [ ] Desligue a caixa, espere 1 minuto e ligue: ela reconecta e a música volta sozinha.
- [ ] O painel mostra a loja conectada, caixa 🟢 e "Sincronizado".
- [ ] Mude a ordem de uma playlist em uso e **Salvar e publicar**: a loja mostra "Aplicado" e segue a ordem nova
      a partir da próxima música.
- [ ] Pelo celular: pular e volume funcionam em até 2 s.
- [ ] Oferta de teste: crie uma oferta "a cada 5 minutos" só para esta loja e use **Tocar oferta agora** em
      Loja → Ao vivo. A música abaixa para 30%, a oferta toca por cima e a música volta (pela D24 o painel não cria
      oferta entre músicas).
- [ ] Oferta vencida não toca: mude a validade da oferta de teste para terminar daqui a 2 minutos; depois disso
      ela não toca mais. Remova a oferta de teste.
- [ ] Logado como funcionário: `services.msc` → **Marumbi Som - Agente da loja** → Parar dá **acesso negado**.
- [ ] Logado como funcionário: um som do Windows (ex.: vídeo no navegador) **não sai** na caixa da loja.
- [ ] Passagem de uma música para outra sem silêncio nem corte seco (ouça 3 trocas).

Se algum item falhar, veja o [runbook](runbook.md) antes de ir embora da loja.
