using System.Globalization;
using System.Text.RegularExpressions;
using MarumbiSom.Agent.Storage;
using MarumbiSom.Contracts.Manifests;

namespace MarumbiSom.Agent.Tools;

/// <summary>
/// <c>--build-manifest</c> (Etapa 2, sem Hub): importa as músicas e ofertas de pastas locais para o cache e grava
/// um manifesto ativo. O serviço em execução percebe a troca em até 5 s, sem reiniciar.
/// </summary>
public static partial class LocalManifestBuilder
{
    public static readonly string[] AudioExtensions = [".m4a", ".mp3", ".aac", ".flac", ".wav", ".ogg", ".opus", ".wma"];

    public const string Usage = """
        Uso (PowerShell como administrador):
          MarumbiSom.Agent.exe --build-manifest --music <pasta> [opções]

        Opções:
          --music <pasta>            músicas da playlist (obrigatório), na ordem alfabética dos arquivos
          --playlist <nome>          nome da playlist (padrão: principal)
          --shuffle                  ordem aleatória
          --start HH:mm --end HH:mm  janela da agenda (padrão: 00:00 a 24:00)
          --days 1,2,3,4,5,6         dias da semana, 0 = domingo (padrão: todos)
          --volume 70                volume da loja, 0 a 100
          --crossfade 5              segundos de mixagem entre músicas (0 = emenda sem mixagem)
          --offers <pasta>           áudios de ofertas
          --offer-every-tracks N     cada oferta toca entre músicas a cada N músicas
          --offer-every-minutes N    cada oferta toca a cada N minutos
          --offer-mode between|during  modo da regra a cada N minutos (padrão: during)
          --min-gap 300              intervalo mínimo entre ofertas, em segundos
          --duck 30                  nível da música durante a oferta, em %
          --store <id>               identificação da loja (padrão: local)
        """;

    public static async Task<int> RunAsync(string[] args, AgentDataPaths paths, ILoggerFactory loggerFactory)
    {
        ArgumentNullException.ThrowIfNull(args);
        ArgumentNullException.ThrowIfNull(loggerFactory);
        var options = ParseOptions(args);
        if (!options.TryGetValue("music", out var musicFolder) || !Directory.Exists(musicFolder))
        {
            Console.Error.WriteLine(musicFolder is null ? "Informe --music <pasta>." : $"Pasta não encontrada: {musicFolder}");
            Console.Error.WriteLine(Usage);
            return 1;
        }

        try
        {
            var store = new ManifestStore(paths, loggerFactory.CreateLogger<ManifestStore>());
            store.Load();
            var cache = new CacheManager(paths, TimeProvider.System, loggerFactory.CreateLogger<CacheManager>());
            var manifest = await BuildAsync(options, cache, store.Active?.Version ?? 0, TimeProvider.System.GetLocalNow(), CancellationToken.None);
            store.SaveActive(manifest);
            Console.WriteLine($"Manifesto versão {manifest.Version} gravado em {paths.ActiveManifest}");
            Console.WriteLine($"  {manifest.Playlists.Values.Sum(p => p.Items.Count)} músicas, {manifest.Spots.Offers.Count} ofertas, mixagem {manifest.CrossfadeS} s, volume {manifest.Volume}");
            foreach (var schedule in manifest.Schedules)
            {
                Console.WriteLine($"  Agenda: {schedule.Start:HH\\:mm}–{(schedule.End == TimeOnly.MaxValue ? "24:00" : schedule.End.ToString("HH\\:mm", CultureInfo.InvariantCulture))} dias {string.Join(',', schedule.Days)} → {schedule.PlaylistId}");
            }

            return 0;
        }
        catch (UnauthorizedAccessException ex)
        {
            Console.Error.WriteLine($"Sem permissão para gravar em {paths.Root} ({ex.Message}). Rode o PowerShell como administrador.");
            return 1;
        }
        catch (FormatException ex)
        {
            Console.Error.WriteLine(ex.Message);
            Console.Error.WriteLine(Usage);
            return 1;
        }
    }

    public static async Task<Manifest> BuildAsync(
        IReadOnlyDictionary<string, string> options, CacheManager cache, long previousVersion, DateTimeOffset now, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(options);
        ArgumentNullException.ThrowIfNull(cache);
        var playlistId = options.GetValueOrDefault("playlist", "principal");
        var tracks = new Dictionary<string, TrackInfo>(StringComparer.Ordinal);
        var items = new List<PlaylistItemRef>();
        foreach (var file in AudioFiles(options["music"]))
        {
            var hash = await cache.ImportAsync(file, cancellationToken);
            tracks[hash] = Info(file, TrackKind.Music);
            items.Add(new PlaylistItemRef($"it-{items.Count + 1}", hash));
            Console.WriteLine($"  música {items.Count}: {Path.GetFileName(file)}");
        }

        if (items.Count == 0)
        {
            throw new FormatException($"Nenhum arquivo de áudio em {options["music"]}.");
        }

        var offers = new Dictionary<string, OfferDefinition>(StringComparer.Ordinal);
        var placements = new List<Placement>();
        if (options.TryGetValue("offers", out var offersFolder))
        {
            var duck = Int(options, "duck", 30, 0, 100);
            foreach (var file in AudioFiles(offersFolder))
            {
                var hash = await cache.ImportAsync(file, cancellationToken);
                tracks[hash] = Info(file, TrackKind.Offer);
                var offerId = $"of-{offers.Count + 1}";
                offers[offerId] = new OfferDefinition
                {
                    Hash = hash,
                    Name = Path.GetFileNameWithoutExtension(file),
                    DuckLevel = duck,
                    ValidFrom = now.AddDays(-1),
                    ValidUntil = now.AddYears(1),
                };
                if (options.ContainsKey("offer-every-tracks"))
                {
                    placements.Add(new EveryTracksPlacement { Id = $"pl-{placements.Count + 1}", OfferId = offerId, N = Int(options, "offer-every-tracks", 3, 1, 1000) });
                }

                if (options.ContainsKey("offer-every-minutes"))
                {
                    placements.Add(new EveryMinutesPlacement
                    {
                        Id = $"pl-{placements.Count + 1}",
                        OfferId = offerId,
                        Minutes = Int(options, "offer-every-minutes", 15, 1, 1440),
                        Mode = options.GetValueOrDefault("offer-mode", "during") == "between" ? SpotMode.Between : SpotMode.During,
                    });
                }

                Console.WriteLine($"  oferta {offerId}: {Path.GetFileName(file)}");
            }
        }

        var days = options.TryGetValue("days", out var daysText)
            ? daysText.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
                .Select(d => int.TryParse(d, NumberStyles.None, CultureInfo.InvariantCulture, out var day) && day is >= 0 and <= 6 ? day : throw new FormatException($"Dia inválido: {d}"))
                .Distinct().Order().ToList()
            : [0, 1, 2, 3, 4, 5, 6];
        var manifest = new Manifest
        {
            StoreId = options.GetValueOrDefault("store", "local"),
            Version = previousVersion + 1,
            GeneratedAt = now,
            Timezone = options.GetValueOrDefault("timezone", "America/Sao_Paulo"),
            Volume = Int(options, "volume", 70, 0, 100),
            CrossfadeS = Int(options, "crossfade", 5, 0, 30),
            Schedules = [new ScheduleEntry(days, Time(options, "start", "00:00"), Time(options, "end", "24:00"), playlistId)],
            Playlists = new Dictionary<string, PlaylistDefinition> { [playlistId] = new(options.ContainsKey("shuffle"), items) },
            Tracks = tracks,
            Spots = new SpotsSection { MinOfferGapS = Int(options, "min-gap", 300, 0, 86400), Offers = offers, Placements = placements },
        };
        return ManifestHasher.WithContentHash(manifest);
    }

    public static Dictionary<string, string> ParseOptions(string[] args)
    {
        ArgumentNullException.ThrowIfNull(args);
        var options = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        for (var i = 0; i < args.Length; i++)
        {
            if (!args[i].StartsWith("--", StringComparison.Ordinal) || args[i].Contains(':', StringComparison.Ordinal))
            {
                continue;
            }

            var name = args[i][2..];
            var hasValue = i + 1 < args.Length && !args[i + 1].StartsWith("--", StringComparison.Ordinal);
            options[name] = hasValue ? args[++i] : "true";
        }

        return options;
    }

    private static IEnumerable<string> AudioFiles(string folder) =>
        Directory.EnumerateFiles(folder)
            .Where(f => AudioExtensions.Contains(Path.GetExtension(f), StringComparer.OrdinalIgnoreCase))
            .Order(StringComparer.OrdinalIgnoreCase);

    /// <summary>
    /// Título e artista pelo nome do arquivo ("01 - Artista - Título.mp3"). Sem ffprobe na loja a duração fica
    /// desconhecida (0); o player usa a duração informada pelo mpv.
    /// </summary>
    private static TrackInfo Info(string file, TrackKind kind)
    {
        var name = TrackNumber().Replace(Path.GetFileNameWithoutExtension(file), string.Empty);
        var separator = name.IndexOf(" - ", StringComparison.Ordinal);
        return new TrackInfo
        {
            Kind = kind,
            Size = new FileInfo(file).Length,
            DurationMs = 0,
            Title = separator > 0 && kind == TrackKind.Music ? name[(separator + 3)..] : name,
            Artist = separator > 0 && kind == TrackKind.Music ? name[..separator] : null,
        };
    }

    private static int Int(IReadOnlyDictionary<string, string> options, string name, int fallback, int min, int max)
    {
        if (!options.TryGetValue(name, out var text))
        {
            return fallback;
        }

        return int.TryParse(text, NumberStyles.Integer, CultureInfo.InvariantCulture, out var value) && value >= min && value <= max
            ? value
            : throw new FormatException($"--{name} deve ser um número entre {min} e {max}.");
    }

    private static TimeOnly Time(IReadOnlyDictionary<string, string> options, string name, string fallback)
    {
        var text = options.GetValueOrDefault(name, fallback);
        if (text == "24:00")
        {
            return TimeOnly.MaxValue;
        }

        return TimeOnly.TryParseExact(text, "HH:mm", CultureInfo.InvariantCulture, DateTimeStyles.None, out var value)
            ? value
            : throw new FormatException($"--{name} deve estar no formato HH:mm.");
    }

    [GeneratedRegex(@"^\d{1,3}\s*[-.]\s+")]
    private static partial Regex TrackNumber();
}
