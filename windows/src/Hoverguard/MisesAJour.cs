using System;
using System.Threading.Tasks;
using Velopack;
using Velopack.Sources;

namespace Hoverguard;

/// <summary>
/// Mises à jour automatiques (Velopack, MIT) lues dans les Releases GitHub de Hoverguard, en HTTPS. Seule connexion
/// réseau de l'app, désactivable dans les réglages. La mise à jour est téléchargée en arrière-plan, puis installée
/// quand <see cref="Coeur.ReglesMiseAJour"/> le permet (jamais pendant une garde) ou quand on quitte Hoverguard.
/// Une copie lancée hors de l'installateur (ancien exe autonome) ne peut pas se mettre à jour toute seule.
/// </summary>
internal sealed class MisesAJour
{
    public const string PageDesVersions = "https://github.com/AnthonyTrtre/hoverguard/releases/latest";
    private readonly UpdateManager gestionnaire = new(new GithubSource("https://github.com/AnthonyTrtre/hoverguard", null, false));
    private UpdateInfo? prete;
    private bool enCours;

    public bool Installee => gestionnaire.IsInstalled;
    public string? VersionPrete => prete?.TargetFullRelease.Version.ToString();

    /// <summary>Cherche et télécharge une nouvelle version. Renvoie la version prête, ou null (à jour, hors ligne…).</summary>
    public async Task<string?> Preparer()
    {
        if (!Installee || enCours) return VersionPrete;
        enCours = true;
        try
        {
            var info = await gestionnaire.CheckForUpdatesAsync();
            if (info is null) return VersionPrete;
            await gestionnaire.DownloadUpdatesAsync(info);
            prete = info;
            Journal.Info($"Mise à jour {VersionPrete} téléchargée");
        }
        catch (Exception e) { Journal.Erreur($"Mises à jour : {e.Message}"); }
        finally { enCours = false; }
        return VersionPrete;
    }

    /// <summary>Installe tout de suite et relance Hoverguard (en mode « démarrage » : rien ne s'ouvre).</summary>
    public void InstallerEtRelancer()
    {
        if (prete is null) return;
        Journal.Info($"Installation de la mise à jour {VersionPrete}");
        gestionnaire.ApplyUpdatesAndRestart(prete.TargetFullRelease, [Coeur.ReglesLancement.ArgumentDemarrage]);
    }

    /// <summary>À la sortie de Hoverguard : la mise à jour prête s'installe sans fenêtre, sans relancer l'app.</summary>
    public void InstallerALaSortie()
    {
        if (prete is null) return;
        try { gestionnaire.WaitExitThenApplyUpdates(prete.TargetFullRelease, silent: true, restart: false); }
        catch (Exception e) { Journal.Erreur($"Mise à jour à la sortie : {e.Message}"); }
    }
}
