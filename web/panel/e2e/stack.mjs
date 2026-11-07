// Sobe o sistema inteiro para o teste ponta a ponta do painel: Hub real servindo o build do painel e um agente em
// modo simulado (mpv e Bluetooth falsos), cada um com uma pasta de dados temporária. Usado pelo webServer do
// playwright.config.ts; termina os dois processos quando o Playwright encerra.
import { spawn, spawnSync } from 'node:child_process';
import { createHash, generateKeyPairSync, sign } from 'node:crypto';
import { mkdtempSync, mkdirSync, writeFileSync } from 'node:fs';
import { tmpdir } from 'node:os';
import { dirname, join, resolve } from 'node:path';
import { fileURLToPath } from 'node:url';
import { crc32 } from 'node:zlib';

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

// Versão do agente assinada para o teste da atualização (D27): par Ed25519 de teste, .zip com o que o Hub exige e o
// .sig.json no formato do sign-release. O agente simulado confia nesta chave pública (Updates:PublicKeyFile).
const releaseVersion = '0.6.900';
const { publicKey, privateKey } = generateKeyPairSync('ed25519');
const publicKeyFile = join(root, 'release-public-key.txt');
writeFileSync(publicKeyFile, Buffer.from(publicKey.export({ format: 'jwk' }).x, 'base64url').toString('base64'));
const releaseZip = join(audio, `MarumbiSom.Agent-${releaseVersion}.zip`);
const releaseSig = join(audio, `MarumbiSom.Agent-${releaseVersion}.sig.json`);
const zipBytes = storedZip([
    ['MarumbiSom.Agent.exe', 'agente de teste'],
    ['MarumbiSom.Updater.exe', 'atualizador de teste'],
    ['release-public-key.txt', 'chave'],
]);
const sha256 = createHash('sha256').update(zipBytes).digest('hex');
writeFileSync(releaseZip, zipBytes);
writeFileSync(
    releaseSig,
    JSON.stringify({
        version: releaseVersion,
        sha256,
        signature: sign(null, Buffer.from(`marumbi-som-agent|${releaseVersion}|${sha256}`), privateKey).toString(
            'base64',
        ),
    }),
);

/** .zip mínimo sem compressão (o Node não traz um gerador de zip). */
function storedZip(files) {
    const locals = [];
    const centrals = [];
    let offset = 0;
    for (const [name, content] of files) {
        const nameBytes = Buffer.from(name);
        const data = Buffer.from(content);
        const crc = crc32(data);
        const local = Buffer.alloc(30);
        local.writeUInt32LE(0x04034b50, 0);
        local.writeUInt16LE(20, 4);
        local.writeUInt32LE(crc, 14);
        local.writeUInt32LE(data.length, 18);
        local.writeUInt32LE(data.length, 22);
        local.writeUInt16LE(nameBytes.length, 26);
        const central = Buffer.alloc(46);
        central.writeUInt32LE(0x02014b50, 0);
        central.writeUInt16LE(20, 4);
        central.writeUInt16LE(20, 6);
        central.writeUInt32LE(crc, 16);
        central.writeUInt32LE(data.length, 20);
        central.writeUInt32LE(data.length, 24);
        central.writeUInt16LE(nameBytes.length, 28);
        central.writeUInt32LE(offset, 42);
        locals.push(local, nameBytes, data);
        centrals.push(central, nameBytes);
        offset += local.length + nameBytes.length + data.length;
    }
    const centralSize = centrals.reduce((sum, b) => sum + b.length, 0);
    const end = Buffer.alloc(22);
    end.writeUInt32LE(0x06054b50, 0);
    end.writeUInt16LE(files.length, 8);
    end.writeUInt16LE(files.length, 10);
    end.writeUInt32LE(centralSize, 12);
    end.writeUInt32LE(offset, 16);
    return Buffer.concat([...locals, ...centrals, end]);
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
            release: { version: releaseVersion, zip: releaseZip, sig: releaseSig },
        },
        null,
        2,
    ),
);

const children = [
    spawn(
        'dotnet',
        [
            hubDll,
            `--webroot=${panelDist}`,
            `--Hub:DataDirectory=${hubData}`,
            `--Hub:Port=${stack.hubPort}`,
            `--Hub:GitHubRepository=`,
        ],
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
            `--Updates:PublicKeyFile=${publicKeyFile}`,
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
