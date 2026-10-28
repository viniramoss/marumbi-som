namespace MarumbiSom.Hub.Panel;

/// <summary>Corpo das respostas de erro da API do painel; a mensagem é mostrada como está.</summary>
public sealed record ApiError(string Error);

public static class ApiResults
{
    public static IResult BadRequest(string error) => Results.BadRequest(new ApiError(error));

    public static IResult Conflict(string error) => Results.Conflict(new ApiError(error));

    /// <summary>Declara no OpenAPI o corpo de sucesso e os erros com <see cref="ApiError"/>.</summary>
    public static RouteHandlerBuilder Returns<T>(this RouteHandlerBuilder builder, int status = StatusCodes.Status200OK, params int[] errors)
    {
        ArgumentNullException.ThrowIfNull(builder);
        builder.Produces<T>(status);
        foreach (var error in errors)
        {
            builder.Produces<ApiError>(error);
        }

        return builder;
    }

    public static RouteHandlerBuilder ReturnsNothing(this RouteHandlerBuilder builder, int status = StatusCodes.Status204NoContent, params int[] errors)
    {
        ArgumentNullException.ThrowIfNull(builder);
        builder.Produces(status);
        foreach (var error in errors)
        {
            builder.Produces<ApiError>(error);
        }

        return builder;
    }
}
