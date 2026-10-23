using System.Text;
using MarumbiSom.Hub.Data;
using MarumbiSom.Hub.Panel;
using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;

namespace MarumbiSom.Hub;

/// <summary>Comandos de linha de comando do Hub (rodar como administrador do Windows).</summary>
internal static class HubCli
{
    /// <summary>
    /// <c>--create-admin &lt;usuário&gt;</c>: cria o usuário do painel ou troca a senha dele. A senha vem do stdin (instalador)
    /// ou é digitada duas vezes sem aparecer na tela.
    /// </summary>
    public static async Task<int> CreateAdminAsync(string[] args, IServiceProvider services)
    {
        var index = Array.FindIndex(args, a => a.Equals("--create-admin", StringComparison.OrdinalIgnoreCase));
        var username = index + 1 < args.Length && !args[index + 1].StartsWith("--", StringComparison.Ordinal) ? args[index + 1].Trim() : "admin";
        string? password;
        if (Console.IsInputRedirected)
        {
            using var input = new StreamReader(Console.OpenStandardInput(), new UTF8Encoding(false));
            password = input.ReadLine();
        }
        else
        {
            password = ReadHidden($"Senha do usuário {username} no painel: ");
            if (ReadHidden("Repita a senha: ") != password)
            {
                Console.Error.WriteLine("As senhas não são iguais.");
                return 1;
            }
        }

        if (PanelAuth.ValidatePassword(password) is { } problem)
        {
            Console.Error.WriteLine(problem);
            return 1;
        }

        await HubApp.InitializeDatabaseAsync(services);
        await using var scope = services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<HubDb>();
        var hasher = scope.ServiceProvider.GetRequiredService<IPasswordHasher<User>>();
        var time = scope.ServiceProvider.GetRequiredService<TimeProvider>();
        var user = await db.Users.SingleOrDefaultAsync(u => u.Username == username);
        if (user is null)
        {
            user = new User { Username = username, PasswordHash = string.Empty, CreatedAt = time.GetUtcNow() };
            db.Users.Add(user);
        }

        user.PasswordHash = hasher.HashPassword(user, password!);
        db.Audit(null, time, "users.password", username);
        await db.SaveChangesAsync();
        Console.WriteLine($"Usuário '{username}' do painel pronto.");
        return 0;
    }

    private static string ReadHidden(string prompt)
    {
        Console.Write(prompt);
        var builder = new StringBuilder();
        while (true)
        {
            var key = Console.ReadKey(intercept: true);
            if (key.Key == ConsoleKey.Enter)
            {
                Console.WriteLine();
                return builder.ToString();
            }

            if (key.Key == ConsoleKey.Backspace)
            {
                if (builder.Length > 0)
                {
                    builder.Length--;
                }
            }
            else if (!char.IsControl(key.KeyChar))
            {
                builder.Append(key.KeyChar);
            }
        }
    }
}
