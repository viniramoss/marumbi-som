using MarumbiSom.Agent.Configuration;
using MarumbiSom.Agent.LocalConfig;
using MarumbiSom.Agent.Storage;
using Microsoft.Extensions.Logging.Abstractions;

namespace MarumbiSom.Agent.Tools;

/// <summary>Comandos de linha de comando para o administrador (rodar como administrador do Windows).</summary>
internal static class AgentCli
{
    /// <summary>Pasta de dados conforme appsettings.json, config.json e <c>--Agent:DataDirectory=...</c>.</summary>
    public static AgentDataPaths ResolvePaths(string[] args, bool simulate = false)
    {
        var configuration = new ConfigurationBuilder()
            .SetBasePath(AppContext.BaseDirectory)
            .AddJsonFile("appsettings.json", optional: true)
            .AddJsonFile(AgentPaths.ConfigFile, optional: true)
            .AddCommandLine(args.Where(a => a.StartsWith("--Agent:", StringComparison.OrdinalIgnoreCase)).ToArray())
            .Build();
        var directory = configuration["Agent:DataDirectory"];
        if (string.IsNullOrWhiteSpace(directory))
        {
            return simulate ? new AgentDataPaths(Path.Combine(AppContext.BaseDirectory, "simulacao")) : AgentDataPaths.Default;
        }

        return new AgentDataPaths(directory);
    }

    /// <summary><c>--set-local-password</c>: define a senha da configuração local (lida do console ou da entrada padrão).</summary>
    public static int SetLocalPassword(string[] args)
    {
        var paths = ResolvePaths(args);
        string? password;
        if (Console.IsInputRedirected)
        {
            password = Console.In.ReadLine();
        }
        else
        {
            password = ReadHidden("Nova senha da configuração local: ");
            if (ReadHidden("Repita a senha: ") != password)
            {
                Console.Error.WriteLine("As senhas não são iguais.");
                return 1;
            }
        }

        if (LocalPassword.Validate(password) is { } problem)
        {
            Console.Error.WriteLine(problem);
            return 1;
        }

        try
        {
            new LocalConfigFile(paths).Set(LocalAuth.Section, LocalAuth.PasswordKey, LocalPassword.Hash(password!));
        }
        catch (UnauthorizedAccessException)
        {
            Console.Error.WriteLine($"Sem permissão para gravar {paths.ConfigFile}. Rode o PowerShell como administrador.");
            return 1;
        }

        Console.WriteLine($"Senha da configuração local definida em {paths.ConfigFile}.");
        return 0;
    }

    public static Task<int> BuildManifestAsync(string[] args) =>
        LocalManifestBuilder.RunAsync(args, ResolvePaths(args), NullLoggerFactory.Instance);

    private static string ReadHidden(string prompt)
    {
        Console.Write(prompt);
        var buffer = new System.Text.StringBuilder();
        while (true)
        {
            var key = Console.ReadKey(intercept: true);
            if (key.Key == ConsoleKey.Enter)
            {
                Console.WriteLine();
                return buffer.ToString();
            }

            if (key.Key == ConsoleKey.Backspace)
            {
                if (buffer.Length > 0)
                {
                    buffer.Length--;
                }
            }
            else if (!char.IsControl(key.KeyChar))
            {
                buffer.Append(key.KeyChar);
            }
        }
    }
}
