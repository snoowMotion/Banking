namespace BudgetTenable.Api.Banking;

/// <summary>Réglages de la synchronisation bancaire (variables d'environnement BUDGET_EB_* et BUDGET_SYNC_HOURS).</summary>
public class EnableBankingOptions
{
    /// <summary>Identifiant de l'application créée dans le portail Enable Banking (environnement Production).</summary>
    public string? AppId { get; set; }
    /// <summary>Clé privée RSA de l'application, au format PEM. Préférer <see cref="PrivateKeyFile"/> (Docker secret).</summary>
    public string? PrivateKey { get; set; }
    public string? PrivateKeyFile { get; set; }
    /// <summary>URL de retour déclarée dans le portail : https://ton-domaine/api/bank/callback.</summary>
    public string? RedirectUrl { get; set; }
    /// <summary>Pays des banques proposées (code ISO à deux lettres).</summary>
    public string Country { get; set; } = "FR";
    /// <summary>
    /// Intervalle entre deux synchros automatiques, en heures ; 0 les désactive.
    /// Six heures par défaut : beaucoup de banques n'acceptent que quatre lectures par jour sans l'utilisateur.
    /// </summary>
    public double SyncHours { get; set; } = 6;
    public string ApiUrl { get; set; } = "https://api.enablebanking.com";

    public bool Configured => !string.IsNullOrWhiteSpace(AppId);
    public TimeSpan SyncInterval => TimeSpan.FromHours(Math.Max(0, SyncHours));
}
