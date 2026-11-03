namespace Hoverguard.Tests;

public class AgentsTests
{
    static readonly DateTime T0 = new(2026, 11, 3, 12, 0, 0, DateTimeKind.Utc);

    [Fact]
    public void TravailAttenteFin()
    {
        var s = new SuiviAgents();
        s.Signaler("Claude a1b2", EtatAgent.Travail, T0);
        s.Signaler("Codex", EtatAgent.Travail, T0);
        Assert.Equal(["Claude a1b2", "Codex"], s.Actifs(T0));
        s.Signaler("Codex", EtatAgent.Attente, T0);
        Assert.Equal(["Claude a1b2"], s.Actifs(T0));
        Assert.Equal(["Codex"], s.EnAttente(T0));
        s.Signaler("Claude a1b2", EtatAgent.Fini, T0);
        Assert.Empty(s.Actifs(T0));
    }

    [Fact]
    public void UnAgentMuetDepuis20MinutesSEteint()
    {
        var s = new SuiviAgents();
        s.Signaler("Claude", EtatAgent.Travail, T0);
        Assert.Equal(["Claude"], s.Actifs(T0.AddMinutes(19)));
        Assert.Empty(s.Actifs(T0.AddMinutes(21)));
    }

    [Fact]
    public void SousAgentsComptesAPart()
    {
        var s = new SuiviAgents();
        s.Signaler("Claude a1", EtatAgent.Travail, T0);
        for (int i = 0; i < 5; i++) s.Signaler("Claude a1", EtatAgent.SousAgentDebut, T0);
        s.Signaler("Claude b2", EtatAgent.SousAgentDebut, T0);  // un sous-agent qui démarre réveille sa session
        s.Signaler("Claude a1", EtatAgent.SousAgentFin, T0);
        Assert.Equal(["Claude a1", "Claude b2"], s.Actifs(T0));
        Assert.Equal(5, s.SousAgents(T0));
        Assert.Equal(7, s.Total(T0));
        s.Signaler("Claude a1", EtatAgent.Fini, T0);
        Assert.Equal(2, s.Total(T0));
        for (int i = 0; i < 3; i++) s.Signaler("Claude b2", EtatAgent.SousAgentFin, T0);
        Assert.Equal(0, s.SousAgents(T0));  // jamais négatif
    }

    [Fact]
    public void CompteurEtEquipe()
    {
        Assert.Equal("Aucun agent au travail", Textes.Compteur(0, 0));
        Assert.Equal("1 agent au travail", Textes.Compteur(1, 0));
        Assert.Equal("11 agents au travail (2 principaux + 9 sous-agents)", Textes.Compteur(2, 9));
        Assert.Equal("2 agents au travail (1 principal + 1 sous-agent)", Textes.Compteur(1, 1));
        Assert.Contains("pause café", Textes.Equipe(0));
        Assert.Contains("11", Textes.Equipe(11));
        Assert.Equal("Un collègue numérique au travail, concentré comme jamais", Textes.Equipe(1));
    }

    [Fact]
    public void Batterie()
    {
        Assert.Null(Textes.Batterie(new(80, true)));
        Assert.Null(Textes.Batterie(new(null, false)));
        Assert.Equal("🔋 Sur batterie (64 %) : merci de me brancher, j'en ai pour un moment.", Textes.Batterie(new(64, false)));
        Assert.Contains("vite", Textes.Batterie(new(12, false)));
    }

    [Fact]
    public void Statut()
    {
        Assert.Equal("Aucun agent en cours", Textes.StatutAgents([], []));
        Assert.Equal("Claude travaille", Textes.StatutAgents(["Claude"], []));
        Assert.Equal("2 agents travaillent : A, B", Textes.StatutAgents(["A", "B"], []));
        Assert.Equal("Codex attend une réponse", Textes.StatutAgents([], ["Codex"]));
    }

    [Fact]
    public void PhrasesDrolesEtPolies()
    {
        Assert.True(Textes.Phrases.Count >= 5);
        Assert.All(Textes.Phrases, p => Assert.InRange(p.Length, 1, 89));
        Assert.DoesNotContain(Textes.Phrases, p => p.Contains("Mac"));
    }
}

public class OrdresTests
{
    [Fact]
    public void Url()
    {
        Assert.Equal(new Ordre.Agent("Claude a1b2", EtatAgent.Travail), Ordre.Lire("hoverguard://agent?nom=Claude%20a1b2&etat=travail"));
        Assert.Equal(new Ordre.Agent("agent", EtatAgent.Fini), Ordre.Lire("hoverguard://agent?etat=fini"));
        Assert.Equal(new Ordre.Verrouiller(), Ordre.Lire("hoverguard://verrouiller"));
        Assert.Equal(new Ordre.Verrouiller(), Ordre.Lire("hoverguard://verrouiller/"));
        Assert.Equal(new Ordre.Agent("C", EtatAgent.SousAgentDebut), Ordre.Lire("hoverguard://agent?nom=C&etat=sous-agent-debut"));
        Assert.Equal(new Ordre.Agent("C", EtatAgent.SousAgentFin), Ordre.Lire("hoverguard://agent/?nom=C&etat=sous-agent-fin"));
        Assert.Null(Ordre.Lire("hoverguard://agent?nom=x&etat=nimporte"));
        Assert.Null(Ordre.Lire("autre://verrouiller"));
        Assert.Null(Ordre.Lire("pas une url"));
        Assert.Null(Ordre.Lire(null));
    }

    [Theory]
    [InlineData("hoverguard://deverrouiller")]
    [InlineData("hoverguard://lever")]
    [InlineData("hoverguard://verrouiller/../deverrouiller")]
    [InlineData("hoverguard://agent/deverrouiller?etat=fini")]
    [InlineData("HOVERGUARD://DEVERROUILLER")]
    public void PersonneNeLeveLaGardeParUrl(string url) => Assert.Null(Ordre.Lire(url));

    [Fact]
    public void NomBorneEtNettoye()
    {
        var o = Assert.IsType<Ordre.Agent>(Ordre.Lire("hoverguard://agent?nom=" + new string('x', 100) + "&etat=travail"));
        Assert.Equal(40, o.Nom.Length);
        var c = Assert.IsType<Ordre.Agent>(Ordre.Lire("hoverguard://agent?nom=%20A%0Ab%20&etat=travail"));
        Assert.Equal("Ab", c.Nom);
        Assert.Equal("agent", Assert.IsType<Ordre.Agent>(Ordre.Lire("hoverguard://agent?nom=%20&etat=travail")).Nom);
    }
}

public class AbsenceTests
{
    [Fact]
    public void RapportDeRetour()
    {
        var t0 = new DateTime(2027, 1, 15, 8, 0, 0);
        var j = new JournalAbsence(t0);
        Assert.Contains("Personne n'a touché au PC", j.Rapport(t0.AddSeconds(30)));
        Assert.Contains("moins d'une minute", j.Rapport(t0.AddSeconds(30)));
        j.Reveil(t0.AddMinutes(10));
        j.MotDePasseFaux();
        j.AgentTermine("Claude a1");
        j.AgentTermine("Claude a1");
        j.SousAgents(4);
        j.SousAgents(2);
        j.Photo(@"C:\x.jpg");
        var r = j.Rapport(t0.AddHours(2).AddMinutes(5));
        Assert.Contains("Absence : 2 h 05.", r);
        Assert.Contains("Agents terminés : Claude a1.", r);
        Assert.Contains("Jusqu'à 4 sous-agents", r);
        Assert.Contains("1 fois (08:10)", r);
        Assert.Contains("Mots de passe faux : 1.", r);
        Assert.Contains("Photos prises par la webcam : 1.", r);
    }

    [Fact]
    public void PhotoALaDeuxiemeErreurPuisToutesLesTrois()
    {
        var j = new JournalAbsence();
        var prises = new List<int>();
        for (int n = 1; n <= 9; n++) { j.MotDePasseFaux(); if (j.FautPhotographier) prises.Add(n); }
        Assert.Equal([2, 5, 8], prises);
    }

    [Fact]
    public void AlertesEspacees()
    {
        var l = new LimiteurAlertes();
        var t0 = new DateTime(2026, 11, 3, 12, 0, 0, DateTimeKind.Utc);
        bool[] reponses = [l.Autoriser(Alerte.Intrus, t0), l.Autoriser(Alerte.Intrus, t0.AddSeconds(60)),
                           l.Autoriser(Alerte.AgentAttend, t0.AddSeconds(60)), l.Autoriser(Alerte.Intrus, t0.AddSeconds(301))];
        Assert.Equal([true, false, true, true], reponses);
        Assert.Contains("ton PC", Alerte.Intrus.Texte("2 mots de passe faux"));
    }
}

public class EveilTests
{
    static readonly DateTime T0 = new(2026, 11, 3, 12, 0, 0, DateTimeKind.Utc);

    [Fact]
    public void DureeFinieExpire()
    {
        var s = SessionEveil.Commencer(Duree.QuinzeMinutes, T0);
        Assert.Equal(TimeSpan.FromSeconds(840), s.Restant(T0.AddSeconds(60)));
        Assert.False(s.EstExpiree(T0.AddSeconds(899)));
        Assert.True(s.EstExpiree(T0.AddSeconds(900)));
        Assert.Equal(TimeSpan.Zero, s.Restant(T0.AddSeconds(5000)));
    }

    [Fact]
    public void SessionIndefinieNExpireJamais()
    {
        var s = SessionEveil.Commencer(Duree.Indefinie, T0);
        Assert.Null(s.Restant(T0.AddYears(30)));
        Assert.False(s.EstExpiree(T0.AddYears(30)));
    }

    [Theory]
    [InlineData(30, "1 min")]
    [InlineData(600, "10 min")]
    [InlineData(3599, "1 h 00")]
    [InlineData(3900, "1 h 05")]
    [InlineData(14400, "4 h 00")]
    public void TexteRestant(double secondes, string attendu) => Assert.Equal(attendu, ReglesEveil.TexteRestant(TimeSpan.FromSeconds(secondes)));

    [Fact]
    public void ArretSeulementSurBatterieSousLeSeuil()
    {
        Assert.True(ReglesEveil.DoitArreter(new(15, false), 20));
        Assert.True(ReglesEveil.DoitArreter(new(20, false), 20));
        Assert.False(ReglesEveil.DoitArreter(new(21, false), 20));
        Assert.False(ReglesEveil.DoitArreter(new(5, true), 20));
        Assert.False(ReglesEveil.DoitArreter(new(5, false), 0));
        Assert.False(ReglesEveil.DoitArreter(new(null, false), 20));
    }

    [Fact]
    public void LibellesDuMenu()
    {
        Assert.Equal(6, Durees.Toutes.Count);
        Assert.Equal("15 minutes", Duree.QuinzeMinutes.Libelle());
        Assert.Equal("Jusqu'à l'arrêt", Duree.Indefinie.Libelle());
    }
}

public class ReglagesTests
{
    [Fact]
    public void ValeursParDefautEtAllerRetour()
    {
        var dossier = Directory.CreateTempSubdirectory("hoverguard-reglages-").FullName;
        try
        {
            var chemin = Path.Combine(dossier, "sous", "reglages.json");
            var r = Reglages.Charger(chemin);
            Assert.Equal(15, r.GardeAutoMinutes);
            Assert.False(r.PhotoIntrus);
            Assert.False(r.RapportAuRetour);  // le rapport attend dans le menu, comme sur macOS
            r.Phrase = "  Bonjour  ";
            r.SeuilBatterie = 30;
            r.DureeParDefaut = Duree.UneHeure;
            r.Notifications = true;
            r.Enregistrer(chemin);
            var relu = Reglages.Charger(chemin);
            Assert.Equal("Bonjour", relu.Phrase);
            Assert.Equal(30, relu.SeuilBatterie);
            Assert.Equal(Duree.UneHeure, relu.DureeParDefaut);
            Assert.False(relu.Notifications);  // pas de canal sous Windows : toujours désactivé
            Assert.Contains("\"phrase\"", File.ReadAllText(chemin));
            File.WriteAllText(chemin, "{ abîmé");
            Assert.Equal(15, Reglages.Charger(chemin).GardeAutoMinutes);
        }
        finally { Directory.Delete(dossier, recursive: true); }
    }
}
