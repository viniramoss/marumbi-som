// Sobe o sistema inteiro para o teste ponta a ponta do painel: Hub real servindo o build do painel e um agente em
// modo simulado (mpv e Bluetooth falsos), cada um com uma pasta de dados temporária. Usado pelo webServer do
// playwright.config.ts; termina os dois processos quando o Playwright encerra.
import { spawn, spawnSync } from 'node:child_process';
import { mkdtempSync, mkdirSync, writeFileSync } from 'node:fs';
import { tmpdir } from 'node:os';
import { dirname, join, resolve } from 'node:path';
import { fileURLToPath } from 'node:url';

const here = dirname(fileURLToPath(import.meta.url));
const repo = resolve(here, '../../..');
const panelDist = resolve(here, '../dist');
const configuration = process.env.E2E_CONFIGURATION ?? 'Debug';
const hubDll = join(repo, `src/MarumbiSom.Hub/bin/${configuration}/net10.0/MarumbiSom.Hub.dll`);
const agentDll = join(
    repo,
    `src/MarumbiSom.Agent/bin/${configuration}/net10.0-windows10.0.19041.0/MarumbiSom.Agent.dll`,
);

export const stack = {
    hubPort: 5180,
    agentPort: 47810,
    adminPassword: 'painel-e2e-1234',
    localPassword: 'local-e2e-1234',
};

const root = mkdtempSync(join(tmpdir(), 'marumbi-e2e-'));
const hubData = join(root, 'hub');
const agentData = join(root, 'agent');
const audio = join(root, 'audio');
for (const dir of [hubData, agentData, audio]) mkdirSync(dir, { recursive: true });

function run(command, args, input) {
    const result = spawnSync(command, args, { input, encoding: 'utf8' });
    if (result.status !== 0) {
        console.error(result.stdout, result.stderr);
        throw new Error(`${command} ${args.join(' ')} falhou (${result.status})`);
    }
}

if (process.env.E2E_SKIP_BUILD !== '1') {
    run('dotnet', ['build', join(repo, 'src/MarumbiSom.Hub'), '-c', configuration, '-v', 'q']);
    run('dotnet', [
        'build',
        join(repo, 'src/MarumbiSom.Agent'),
        '-c',
        configuration,
        '-v',
        'q',
        '-p:EnableWindowsTargeting=true',
    ]);
}

// Duas músicas curtas geradas pelo ffmpeg (o mesmo que o Hub usa na ingestão); o Hub só aceita de 30 s para cima.
const tracks = [
    ['Trio Marumbi - Abertura do Açougue.mp3', 'sine=frequency=440:duration=40'],
    ['Banda da Picanha - Fim de Tarde.mp3', 'sine=frequency=660:duration=45'],
];
// Áudio de oferta: o perfil de ofertas aceita de 3 s a 2 min (seção 6.5).
const offerAudio = ['Oferta Picanha.mp3', 'sine=frequency=880:duration=8'];
for (const [name, source] of [...tracks, offerAudio]) {
    run('ffmpeg', [
        '-hide_banner',
        '-loglevel',
        'error',
        '-y',
        '-f',
        'lavfi',
        '-i',
        source,
        '-ac',
        '2',
        join(audio, name),
    ]);
}

run('dotnet', [hubDll, '--create-admin', 'admin', `--Hub:DataDirectory=${hubData}`], `${stack.adminPassword}\n`);
run('dotnet', [agentDll, '--set-local-password', `--Agent:DataDirectory=${agentData}`], `${stack.localPassword}\n`);

writeFileSync(
    join(here, '.stack.json'),
    JSON.stringify(
        {
            ...stack,
            root,
            audio,
            tracks: tracks.map(([name]) => join(audio, name)),
            offerAudio: join(audio, offerAudio[0]),
        },
        null,
        2,
    ),
);

const children = [
    spawn(
        'dotnet',
        [hubDll, `--webroot=${panelDist}`, `--Hub:DataDirectory=${hubData}`, `--Hub:Port=${stack.hubPort}`],
        {
            stdio: 'inherit',
        },
    ),
    spawn(
        'dotnet',
        [
            agentDll,
            '--simulate',
            `--Agent:DataDirectory=${agentData}`,
            `--LocalConfig:Port=${stack.agentPort}`,
            '--Bluetooth:SpeakerAddress=00:11:22:33:44:55',
        ],
        { stdio: 'inherit' },
    ),
];

const stop = () => {
    for (const child of children) child.kill('SIGTERM');
    process.exit(0);
};
process.on('SIGTERM', stop);
process.on('SIGINT', stop);
for (const child of children) {
    child.on('exit', (code) => {
        console.error(`processo ${child.spawnargs.slice(0, 2).join(' ')} saiu com ${code}`);
        stop();
    });
}
