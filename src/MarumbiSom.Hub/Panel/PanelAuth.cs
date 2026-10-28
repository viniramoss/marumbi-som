using System.Security.Claims;
using System.Text.Json;
using MarumbiSom.Hub.Data;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authentication.Cookies;
using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;

namespace MarumbiSom.Hub.Panel;

/// <summary>
/// Login do painel (seção 12.1): usuário e senha com o <see cref="PasswordHasher{TUser}"/>, cookie HttpOnly, Secure e
/// SameSite=Strict, no máximo 5 tentativas de login por minuto, e cabeçalho <c>X-MarumbiSom</c> em toda escrita (CSRF).
/// </summary>
public static class PanelAuth
{
    public const string Scheme = CookieAuthenticationDefaults.AuthenticationScheme;
    public const string CookieName = "marumbisom_painel";
    public const string CsrfHeader = "X-MarumbiSom";
    public const string LoginRateLimit = "login";
    public const string Policy = "painel";
    public const int MinPasswordLength = 10;

    public static IServiceCollection AddPanelAuth(this IServiceCollection services)
    {
        services.AddSingleton<IPasswordHasher<User>, PasswordHasher<User>>();
        services.AddAuthentication(Scheme).AddCookie(Scheme, options =>
        {
            options.Cookie.Name = CookieName;
            options.Cookie.HttpOnly = true;
            options.Cookie.SecurePolicy = CookieSecurePolicy.Always;
            options.Cookie.SameSite = SameSiteMode.Strict;
            // Seção 7.2: sessão de 30 dias por dispositivo, renovada enquanto o painel é usado.
            options.ExpireTimeSpan = TimeSpan.FromDays(30);
            options.SlidingExpiration = true;
            // API: responde 401/403 em vez de redirecionar para uma página de login.
            options.Events.OnRedirectToLogin = context =>
            {
                context.Response.StatusCode = StatusCodes.Status401Unauthorized;
                return Task.CompletedTask;
            };
            options.Events.OnRedirectToAccessDenied = context =>
            {
                context.Response.StatusCode = StatusCodes.Status403Forbidden;
                return Task.CompletedTask;
            };
        });
        services.AddAuthorizationBuilder().AddPolicy(Policy, policy => policy.AddAuthenticationSchemes(Scheme).RequireAuthenticatedUser());
        return services;
    }

    /// <summary>
    /// Toda requisição de escrita em /api exige o cabeçalho <c>X-MarumbiSom: 1</c>. Um site de fora não consegue enviá-lo
    /// sem CORS (que o Hub não habilita), então o cookie sozinho nunca basta para alterar algo.
    /// </summary>
    public static IApplicationBuilder UseCsrfHeader(this IApplicationBuilder app) => app.Use(async (context, next) =>
    {
        var request = context.Request;
        if (request.Path.StartsWithSegments("/api", StringComparison.Ordinal)
            && !HttpMethods.IsGet(request.Method) && !HttpMethods.IsHead(request.Method) && !HttpMethods.IsOptions(request.Method)
            && request.Headers[CsrfHeader] != "1")
        {
            context.Response.StatusCode = StatusCodes.Status403Forbidden;
            await context.Response.WriteAsJsonAsync(new { error = "Cabeçalho X-MarumbiSom ausente." });
            return;
        }

        await next();
    });

    public static void MapPanelAuth(this IEndpointRouteBuilder app)
    {
        var auth = app.MapGroup("/api/auth");
        auth.MapPost("/login", LoginAsync).RequireRateLimiting(LoginRateLimit);
        auth.MapPost("/logout", async (HttpContext context) =>
        {
            await context.SignOutAsync(Scheme);
            return Results.Ok(new { ok = true });
        });
        auth.MapGet("/me", (ClaimsPrincipal user) => user.Identity?.IsAuthenticated == true
            ? Results.Ok(new { username = user.Identity.Name })
            : Results.Unauthorized());
    }

    public static int? UserId(this ClaimsPrincipal user) =>
        int.TryParse(user.FindFirstValue(ClaimTypes.NameIdentifier), out var id) ? id : null;

    public static void Audit(this HubDb db, ClaimsPrincipal? user, TimeProvider time, string action, string? target, object? data = null) =>
        db.AuditLog.Add(new AuditEntry
        {
            UserId = user?.UserId(),
            Action = action,
            Target = target,
            DataJson = data is null ? null : JsonSerializer.Serialize(data, HubJson.Options),
            CreatedAt = time.GetUtcNow(),
        });

    public static string? ValidatePassword(string? password) =>
        string.IsNullOrEmpty(password) || password.Length < MinPasswordLength
            ? $"A senha precisa ter pelo menos {MinPasswordLength} caracteres."
            : null;

    private static async Task<IResult> LoginAsync(
        LoginRequest request, HttpContext context, HubDb db, IPasswordHasher<User> hasher, TimeProvider time, ILoggerFactory loggers)
    {
        var logger = loggers.CreateLogger(typeof(PanelAuth).FullName!);
        var user = await db.Users.SingleOrDefaultAsync(u => u.Username == request.Username, context.RequestAborted);
        var result = user is null ? PasswordVerificationResult.Failed : hasher.VerifyHashedPassword(user, user.PasswordHash, request.Password ?? string.Empty);
        if (user is null || result == PasswordVerificationResult.Failed)
        {
            logger.LogWarning("Login recusado no painel para '{Username}'", request.Username);
            db.Audit(null, time, "login.failed", request.Username);
            await db.SaveChangesAsync(context.RequestAborted);
            return Results.Json(new { error = "Usuário ou senha incorretos." }, statusCode: StatusCodes.Status401Unauthorized);
        }

        if (result == PasswordVerificationResult.SuccessRehashNeeded)
        {
            user.PasswordHash = hasher.HashPassword(user, request.Password!);
        }

        var identity = new ClaimsIdentity(
            [new Claim(ClaimTypes.NameIdentifier, user.Id.ToString(System.Globalization.CultureInfo.InvariantCulture)), new Claim(ClaimTypes.Name, user.Username)],
            Scheme);
        // Persistente: o cookie sobrevive a fechar o navegador do celular.
        await context.SignInAsync(Scheme, new ClaimsPrincipal(identity), new AuthenticationProperties { IsPersistent = true });
        db.Audit(new ClaimsPrincipal(identity), time, "login", user.Username);
        await db.SaveChangesAsync(context.RequestAborted);
        logger.LogInformation("Login no painel: {Username}", user.Username);
        return Results.Ok(new { username = user.Username });
    }

    public sealed record LoginRequest(string Username, string? Password);
}
