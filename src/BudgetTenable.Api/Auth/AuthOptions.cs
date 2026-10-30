namespace BudgetTenable.Api.Auth;

public class AuthOptions
{
    /// <summary>"pin" (défaut) ou "none" (authentification déléguée au reverse proxy).</summary>
    public string Mode { get; set; } = "pin";
    public string? Pin { get; set; }
    /// <summary>Chemin d'un fichier contenant le PIN (Docker secret).</summary>
    public string? PinFile { get; set; }
    /// <summary>Clé de signature des cookies, en base64 (32 octets minimum). Générée dans le dossier de données si absente.</summary>
    public string? CookieKey { get; set; }
    public int SessionDays { get; set; } = 30;
    /// <summary>À n'activer qu'en développement local, sans HTTPS.</summary>
    public bool InsecureCookies { get; set; }
    public int LoginPermitPerMinute { get; set; } = 5;

    public bool PinEnabled => string.Equals(Mode, "pin", StringComparison.OrdinalIgnoreCase);
}
