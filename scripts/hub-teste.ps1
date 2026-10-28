<#
.SYNOPSIS
    Atalhos para testar o Hub pela API enquanto o painel (Etapa 4) não existe.

.DESCRIPTION
    Roda no PC do Hub, contra http://127.0.0.1:5080. Pede a senha do painel a cada execução.

    Ações:
      enviar      Envia todos os áudios de uma pasta e espera o processamento (mostra volume original e resultado).
      montar      Cria a playlist "Teste" com todas as músicas, a loja e o horário de 00:00 a 24:00 todos os dias,
                  e mostra o código de vinculação. Pode rodar de novo: reaproveita a playlist e a loja.
      codigo      Gera um código de vinculação novo para a loja (vale 30 minutos).
      inverter    Inverte a ordem da playlist "Teste" (aceite: aplicado na loja em até 60 s, sem cortar a música).
      atualizar   Coloca na playlist "Teste" todas as músicas da biblioteca que ainda não estão nela.
      status      Mostra as lojas: conectada, versão aplicada, música, caixa e volume.
      volume      Manda o comando de volume ao vivo para a loja (-Valor 0 a 100).

.EXAMPLE
    .\hub-teste.ps1 enviar -Pasta C:\musicas-teste

.EXAMPLE
    .\hub-teste.ps1 montar -Loja "Loja 1 - Matriz"
#>
[CmdletBinding()]
param(
    [Parameter(Mandatory = $true, Position = 0)]
    [ValidateSet('enviar', 'montar', 'codigo', 'inverter', 'atualizar', 'status', 'volume')]
    [string] $Acao,
    [string] $Pasta,
    [string] $Loja = 'Loja 1 - Matriz',
    [int] $Valor = 70,
    [string] $Usuario = 'admin',
    [string] $Hub = 'http://127.0.0.1:5080',
    [string] $Playlist = 'Teste'
)

$ErrorActionPreference = 'Stop'
Add-Type -AssemblyName System.Net.Http

# O cookie do painel é Secure (feito para o https do Tailscale); aqui, em http local, ele vai à mão no cabeçalho.
$handler = New-Object System.Net.Http.HttpClientHandler
$handler.UseCookies = $false
$client = New-Object System.Net.Http.HttpClient($handler)
$client.BaseAddress = New-Object System.Uri(($Hub.TrimEnd('/') + '/'))
$client.Timeout = [TimeSpan]::FromMinutes(30)
$client.DefaultRequestHeaders.Add('X-MarumbiSom', '1')

function Send-Api([string] $Method, [string] $Path, $Body = $null, $Content = $null) {
    $request = New-Object System.Net.Http.HttpRequestMessage((New-Object System.Net.Http.HttpMethod($Method)), $Path.TrimStart('/'))
    if ($null -ne $Body) {
        $json = ConvertTo-Json -InputObject $Body -Depth 10 -Compress
        $request.Content = New-Object System.Net.Http.StringContent($json, [Text.Encoding]::UTF8, 'application/json')
    } elseif ($null -ne $Content) {
        $request.Content = $Content
    }
    $response = $client.SendAsync($request).GetAwaiter().GetResult()
    $text = $response.Content.ReadAsStringAsync().GetAwaiter().GetResult()
    if (-not $response.IsSuccessStatusCode) {
        $message = $text
        try { $message = ($text | ConvertFrom-Json).error } catch { }
        throw "O Hub respondeu $([int]$response.StatusCode) em $Method $Path`: $message"
    }
    if ($text) { return $text | ConvertFrom-Json }
}

function Connect-Hub {
    $secure = Read-Host -AsSecureString "Senha do usuário '$Usuario' do painel"
    $password = [Runtime.InteropServices.Marshal]::PtrToStringBSTR([Runtime.InteropServices.Marshal]::SecureStringToBSTR($secure))
    $json = ConvertTo-Json -InputObject @{ username = $Usuario; password = $password } -Compress
    $content = New-Object System.Net.Http.StringContent($json, [Text.Encoding]::UTF8, 'application/json')
    $response = $client.PostAsync('api/auth/login', $content).GetAwaiter().GetResult()
    $password = $null
    if ([int]$response.StatusCode -eq 429) {
        throw 'O painel aceita 5 entradas por minuto e cada execução deste script é uma. Espere 1 minuto e rode de novo.'
    }
    if (-not $response.IsSuccessStatusCode) {
        throw "Login recusado ($([int]$response.StatusCode)). Confira a senha."
    }
    $cookie = ($response.Headers.GetValues('Set-Cookie') | Where-Object { $_ -like 'marumbisom_painel=*' } | Select-Object -First 1).Split(';')[0]
    $client.DefaultRequestHeaders.Add('Cookie', $cookie)
}

function Get-TestPlaylist {
    $found = @(Send-Api GET 'api/playlists') | Where-Object { $_.name -eq $Playlist } | Select-Object -First 1
    if ($found) { return Send-Api GET "api/playlists/$($found.id)" }
    return $null
}

function Get-TestStore {
    return @(Send-Api GET 'api/stores') | Where-Object { $_.name -eq $Loja } | Select-Object -First 1
}

function Show-Code([int] $StoreId) {
    $code = Send-Api POST "api/stores/$StoreId/enroll-code" @{}
    $expires = ([DateTimeOffset]::Parse($code.expiresAt)).ToLocalTime().ToString('HH:mm')
    Write-Host ""
    Write-Host "Código de vinculação: $($code.code)  (vale até $expires)" -ForegroundColor Green
    Write-Host "No PC da loja: Configurar Som > Hub > endereço https do Hub + este código."
}

Connect-Hub

switch ($Acao) {
    'enviar' {
        if (-not $Pasta -or -not (Test-Path $Pasta)) { throw 'Informe a pasta com -Pasta C:\caminho\das\musicas' }
        $files = @(Get-ChildItem -Path $Pasta -File | Where-Object { $_.Extension -match '^\.(mp3|m4a|aac|flac|wav|ogg|opus|wma)$' })
        if ($files.Count -eq 0) { throw "Nenhum áudio em $Pasta" }
        $ids = @()
        foreach ($file in $files) {
            $form = New-Object System.Net.Http.MultipartFormDataContent
            $stream = [IO.File]::OpenRead($file.FullName)
            try {
                $part = New-Object System.Net.Http.StreamContent($stream)
                $form.Add($part, 'files', $file.Name)
                $jobs = @(Send-Api POST 'api/tracks' -Content $form)
            } finally {
                $stream.Dispose()
            }
            foreach ($job in $jobs) {
                if ($job.status -eq 'rejected') { Write-Warning "$($file.Name): $($job.error)" } else { $ids += $job.id }
            }
            Write-Host "Enviado: $($file.Name)"
        }
        Write-Host "Processando $($ids.Count) arquivo(s) (corte de silêncio, fade, volume, AAC)..."
        do {
            Start-Sleep -Seconds 3
            $jobs = @(Send-Api GET 'api/uploads') | Where-Object { $ids -contains $_.id }
            $waiting = @($jobs | Where-Object { $_.status -in 'queued', 'processing' }).Count
            Write-Host "  faltam $waiting"
        } while ($waiting -gt 0)
        $tracks = @(Send-Api GET 'api/tracks')
        foreach ($job in $jobs) {
            $track = $tracks | Where-Object { $_.hash -eq $job.hash } | Select-Object -First 1
            switch ($job.status) {
                'done' { Write-Host ("OK          {0}  ->  {1} - {2}  ({3:0.0} s, volume original {4:0.0} LUFS, agora -16 LUFS)" -f $job.originalName, $track.artist, $track.title, ($track.durationMs / 1000), $track.loudnessLufs) -ForegroundColor Green }
                'duplicate' { Write-Host "Duplicada   $($job.originalName) (já estava na biblioteca)" -ForegroundColor Yellow }
                default { Write-Host "Recusada    $($job.originalName): $($job.error)" -ForegroundColor Red }
            }
        }
    }
    'montar' {
        $music = @(Send-Api GET 'api/tracks')
        if ($music.Count -eq 0) { throw 'A biblioteca está vazia. Rode primeiro: .\hub-teste.ps1 enviar -Pasta ...' }
        $list = Get-TestPlaylist
        if (-not $list) {
            $created = Send-Api POST 'api/playlists' @{ name = $Playlist }
            Send-Api PUT "api/playlists/$($created.id)/items" @($music | ForEach-Object { @{ hash = $_.hash } }) | Out-Null
            $list = Get-TestPlaylist
            Write-Host "Playlist '$Playlist' criada com $($music.Count) música(s)."
        } else {
            Write-Host "Playlist '$Playlist' já existe ($(@($list.items).Count) música(s))."
        }
        $store = Get-TestStore
        if (-not $store) {
            $store = (Send-Api POST 'api/stores' @{ name = $Loja }).status
            Write-Host "Loja '$Loja' criada ($($store.slug))."
        }
        Send-Api PUT "api/stores/$($store.id)/schedules" @(@{ days = @(0, 1, 2, 3, 4, 5, 6); start = '00:00'; end = '24:00'; playlistId = $list.id }) | Out-Null
        Write-Host "Horário: todos os dias, 00:00 a 24:00, playlist '$Playlist'."
        Show-Code $store.id
    }
    'codigo' {
        $store = Get-TestStore
        if (-not $store) { throw "Loja '$Loja' não encontrada. Rode: .\hub-teste.ps1 montar" }
        Show-Code $store.id
    }
    'inverter' {
        $list = Get-TestPlaylist
        if (-not $list) { throw "Playlist '$Playlist' não encontrada. Rode: .\hub-teste.ps1 montar" }
        $items = @($list.items)
        [array]::Reverse($items)
        Send-Api PUT "api/playlists/$($list.id)/items" @($items | ForEach-Object { @{ id = $_.id; hash = $_.hash } }) | Out-Null
        Write-Host "Ordem invertida. Primeira agora: $($items[0].title). A loja deve receber em até 60 s e trocar no fim da música atual."
    }
    'atualizar' {
        $list = Get-TestPlaylist
        if (-not $list) { throw "Playlist '$Playlist' não encontrada. Rode: .\hub-teste.ps1 montar" }
        $current = @($list.items)
        $known = @($current | ForEach-Object { $_.hash })
        $new = @(Send-Api GET 'api/tracks') | Where-Object { $known -notcontains $_.hash }
        $body = @($current | ForEach-Object { @{ id = $_.id; hash = $_.hash } }) + @($new | ForEach-Object { @{ hash = $_.hash } })
        Send-Api PUT "api/playlists/$($list.id)/items" $body | Out-Null
        Write-Host "$(@($new).Count) música(s) nova(s) no fim da playlist '$Playlist'. A loja baixa, confere e só então troca."
    }
    'status' {
        foreach ($store in @(Send-Api GET 'api/stores')) {
            $online = if ($store.online) { 'conectada' } else { 'desconectada' }
            $seen = if ($store.lastSeenAt) { ([DateTimeOffset]::Parse($store.lastSeenAt)).ToLocalTime().ToString('dd/MM HH:mm:ss') } else { 'nunca' }
            Write-Host ("{0}: {1} (visto {2}) | versão aplicada {3} de {4} | player {5} | caixa {6} | volume {7}" -f `
                $store.name, $online, $seen, $store.appliedVersion, $store.latestVersion, $store.player, $store.bluetooth, $store.volume)
        }
    }
    'volume' {
        $store = Get-TestStore
        if (-not $store) { throw "Loja '$Loja' não encontrada." }
        $result = Send-Api POST "api/stores/$($store.id)/commands" @{ command = 'setVolume'; args = @{ volume = $Valor } }
        Write-Host "Volume $Valor`: $($result.result) $($result.error)"
    }
}
