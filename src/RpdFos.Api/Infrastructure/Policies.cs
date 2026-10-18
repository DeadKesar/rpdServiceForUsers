namespace RpdFos.Api.Infrastructure;

/// <summary>Политики доступа к API (роли — из токена доступа).</summary>
public static class Policies
{
    public const string MethodistOrAdmin = "methodist-or-admin";
    public const string Admin = "admin-only";
}
