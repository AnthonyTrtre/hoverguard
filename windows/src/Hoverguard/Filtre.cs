using System;
using System.Runtime.InteropServices;
using System.Threading;
using Hoverguard.Coeur;

namespace Hoverguard;

/// <summary>
/// Filtre clavier et souris par crochets bas niveau (<c>WH_KEYBOARD_LL</c>, <c>WH_MOUSE_LL</c>). Windows marque les
/// événements fabriqués par un logiciel (<c>SendInput</c> : Claude, Codex, outils d'automatisation) avec
/// <c>LLKHF_INJECTED</c> / <c>LLMHF_INJECTED</c> : ils passent toujours ; le vrai matériel est filtré par
/// <see cref="SuiviEntrees"/> et <see cref="Regles"/>.
/// <para>
/// Les crochets vivent sur un fil dédié avec sa propre boucle de messages : Windows retire en silence un crochet qui
/// met plus de <c>LowLevelHooksTimeout</c> (≈ 300 ms à 1 s) à répondre, et le fil de l'interface peut être occupé.
/// Comme Windows ne prévient pas de ce retrait, une « sonde » (touche et déplacement injectés, marqués et avalés) vérifie
/// régulièrement pendant la garde que le crochet répond encore ; sinon il est reposé, et s'il ne repart pas la garde
/// le contrôleur verrouille la vraie session Windows (<see cref="SurPanne"/>), comme sur macOS.
/// </para>
/// <para>
/// Pendant la demande de mot de passe, AUCUNE touche physique n'atteint les apps : le crochet les avale toutes, les
/// traduit lui-même en caractères (<c>ToUnicodeEx</c>, disposition du clavier de la fenêtre active) et les passe au
/// contrôleur (<see cref="SurSaisie"/>), qui remplit le mot de passe. La fenêtre d'identification ne prend pas le
/// clavier : l'app de l'agent le garde.
/// </para>
/// </summary>
internal sealed class Filtre : IDisposable
{
    private static readonly UIntPtr MarqueSonde = new(0x486F7672);  // « Hovr »
    private const uint MsgReposer = Natif.WM_APP + 1, MsgOublierToucheMorte = Natif.WM_APP + 2;

    private readonly SuiviEntrees suivi = new();
    private readonly Natif.HookProc procClavier, procSouris;
    private IntPtr crochetClavier, crochetSouris;
    private Thread? fil;
    private uint idFil;
    private volatile int etat = (int)EtatGarde.Libre;
    private long derniereEntreePhysique = Environment.TickCount64;
    private long derniereSondeClavier, derniereSondeSouris;
    private long evenementsVus;

    /// <summary>Appelés sur le fil du crochet : ne rien y faire de long (poster vers l'interface).</summary>
    public Action? SurPoser, SurReveil, SurPanne;
    /// <summary>Touche tapée pendant la demande de mot de passe (fil du crochet).</summary>
    public Action<ActionSaisie>? SurSaisie;
    /// <summary>Une touche morte (« ^ », « ¨ ») a été tapée pendant la saisie et attend la lettre suivante.</summary>
    private bool toucheMorte;
    private readonly byte[] etatClavier = new byte[256];
    private readonly char[] caracteres = new char[8];
    private IntPtr derniereDisposition;

    public Filtre()
    {
        procClavier = Clavier;
        procSouris = Souris;
    }

    public EtatGarde Etat
    {
        get => (EtatGarde)etat;
        set
        {
            var avant = (EtatGarde)Interlocked.Exchange(ref etat, (int)value);
            // Une touche morte laissée en attente se combinerait avec la prochaine lettre tapée ailleurs.
            if (avant == EtatGarde.Authentification && value != EtatGarde.Authentification && fil is not null)
                Natif.PostThreadMessage(idFil, MsgOublierToucheMorte, IntPtr.Zero, IntPtr.Zero);
        }
    }
    public bool Actif => crochetClavier != IntPtr.Zero && crochetSouris != IntPtr.Zero;
    public long EvenementsVus => Interlocked.Read(ref evenementsVus);

    /// <summary>Temps depuis la dernière action sur le VRAI clavier ou la VRAIE souris. Contrairement à
    /// <c>GetLastInputInfo</c>, les entrées injectées par les agents ne comptent pas.</summary>
    public TimeSpan InactivitePhysique => TimeSpan.FromMilliseconds(Environment.TickCount64 - Interlocked.Read(ref derniereEntreePhysique));

    public bool Demarrer()
    {
        if (fil is not null) return Actif;
        using var pret = new ManualResetEventSlim();
        fil = new Thread(() =>
        {
            idFil = Natif.GetCurrentThreadId();
            Natif.PeekMessage(out _, IntPtr.Zero, 0, 0, 0);  // crée la file de messages du fil
            Poser();
            pret.Set();
            while (Natif.GetMessage(out var msg, IntPtr.Zero, 0, 0) > 0)
            {
                if (msg.message == MsgReposer) { Retirer(); Poser(); }
                else if (msg.message == MsgOublierToucheMorte) OublierToucheMorte();
            }
            Retirer();
        }) { IsBackground = true, Name = "Hoverguard filtre", Priority = ThreadPriority.Highest };
        fil.SetApartmentState(ApartmentState.STA);
        fil.Start();
        pret.Wait(5000);
        if (!Actif) Journal.Erreur($"Crochets bas niveau refusés (erreur {Marshal.GetLastWin32Error()})");
        return Actif;
    }

    private void Poser()
    {
        var module = Natif.GetModuleHandle(null);
        crochetClavier = Natif.SetWindowsHookEx(Natif.WH_KEYBOARD_LL, procClavier, module, 0);
        crochetSouris = Natif.SetWindowsHookEx(Natif.WH_MOUSE_LL, procSouris, module, 0);
    }

    private void Retirer()
    {
        if (crochetClavier != IntPtr.Zero) Natif.UnhookWindowsHookEx(crochetClavier);
        if (crochetSouris != IntPtr.Zero) Natif.UnhookWindowsHookEx(crochetSouris);
        crochetClavier = crochetSouris = IntPtr.Zero;
    }

    /// <summary>Vérifie que le crochet répond (pendant la garde). À appeler toutes les quelques secondes.</summary>
    public void Sonder()
    {
        if (fil is null || Interlocked.Exchange(ref sondeEnCours, 1) == 1) return;
        long clavier = Interlocked.Read(ref derniereSondeClavier), souris = Interlocked.Read(ref derniereSondeSouris);
        bool Repond() => Interlocked.Read(ref derniereSondeClavier) != clavier && Interlocked.Read(ref derniereSondeSouris) != souris;
        if (!EnvoyerSonde()) { sondeEnCours = 0; return; }  // session verrouillée, bureau sécurisé…
        ThreadPool.QueueUserWorkItem(_ =>
        {
            try
            {
                Thread.Sleep(1500);
                if (Repond()) return;
                Journal.Erreur("Le crochet ne répond plus : on le repose");
                Natif.PostThreadMessage(idFil, MsgReposer, IntPtr.Zero, IntPtr.Zero);
                Thread.Sleep(500);
                if (!EnvoyerSonde()) return;
                Thread.Sleep(2000);
                if (!Repond()) SurPanne?.Invoke();
            }
            finally { sondeEnCours = 0; }
        });
    }

    private int sondeEnCours;

    /// <summary>Une touche virtuelle non attribuée et un déplacement d'un pixel, injectés et marqués : le crochet les
    /// reconnaît et les avale, ils n'atteignent donc jamais une application ni ne bougent le pointeur. (Un déplacement
    /// nul ne suffit pas : mesuré sur le runner de la CI, Windows ne le transmet pas toujours au crochet.)</summary>
    private static bool EnvoyerSonde()
        => Natif.Envoyer(Natif.Touche(Touches.Masque, false, MarqueSonde), Natif.Touche(Touches.Masque, true, MarqueSonde),
                         Natif.Souris(1, 0, Natif.MOUSEEVENTF_MOVE, MarqueSonde)) == 3;

    /// <summary>Touche « masque » injectée après le raccourci : sans elle, relâcher la touche Windows ouvrirait le
    /// menu Démarrer, puisque le W a été avalé.</summary>
    public static void Masquer() => Natif.Envoyer(Natif.Touche(Touches.Masque, false), Natif.Touche(Touches.Masque, true));

    private IntPtr Clavier(int nCode, IntPtr wParam, IntPtr lParam)
    {
        if (nCode >= 0)
        {
            Interlocked.Increment(ref evenementsVus);
            var k = Marshal.PtrToStructure<Natif.KBDLLHOOKSTRUCT>(lParam);
            bool injectee = (k.flags & Natif.LLKHF_INJECTED) != 0;
            if (injectee && k.dwExtraInfo == MarqueSonde)
            {
                Interlocked.Exchange(ref derniereSondeClavier, Environment.TickCount64 + 1);
                return new IntPtr(1);  // la sonde s'arrête ici
            }
            bool enfoncee = (k.flags & Natif.LLKHF_UP) == 0;
            if (!injectee) Interlocked.Exchange(ref derniereEntreePhysique, Environment.TickCount64);
            var v = suivi.Clavier((int)k.vkCode, enfoncee, injectee, Etat);
            if (v.Decision == Decision.Saisir)
            {
                if (Traduire(k) is ActionSaisie a) SurSaisie?.Invoke(a);
                return new IntPtr(1);  // avalée : rien n'atteint la fenêtre active
            }
            if (Appliquer(v)) return new IntPtr(1);
        }
        return Natif.CallNextHookEx(IntPtr.Zero, nCode, wParam, lParam);
    }

    private IntPtr Souris(int nCode, IntPtr wParam, IntPtr lParam)
    {
        if (nCode >= 0)
        {
            Interlocked.Increment(ref evenementsVus);
            var m = Marshal.PtrToStructure<Natif.MSLLHOOKSTRUCT>(lParam);
            bool injectee = (m.flags & Natif.LLMHF_INJECTED) != 0;
            if (injectee && m.dwExtraInfo == MarqueSonde)
            {
                Interlocked.Exchange(ref derniereSondeSouris, Environment.TickCount64 + 1);
                return new IntPtr(1);  // la sonde s'arrête ici
            }
            // Le toucher et le stylet (écran, pavé tactile de précision) peuvent arriver marqués « injectés » : leur
            // signature les ramène au matériel.
            injectee = !Provenance.EstMaterielle(injectee, m.dwExtraInfo.ToUInt64());
            if (!injectee) Interlocked.Exchange(ref derniereEntreePhysique, Environment.TickCount64);
            int message = wParam.ToInt32();
            int? bouton = message switch
            {
                Natif.WM_LBUTTONDOWN or Natif.WM_LBUTTONUP => 0,
                Natif.WM_RBUTTONDOWN or Natif.WM_RBUTTONUP => 1,
                Natif.WM_MBUTTONDOWN or Natif.WM_MBUTTONUP => 2,
                Natif.WM_XBUTTONDOWN or Natif.WM_XBUTTONUP => 2 + (int)(m.mouseData >> 16),
                _ => null,
            };
            bool enfonce = message is Natif.WM_LBUTTONDOWN or Natif.WM_RBUTTONDOWN or Natif.WM_MBUTTONDOWN or Natif.WM_XBUTTONDOWN;
            if (Appliquer(suivi.Souris(bouton, enfonce, injectee, Etat))) return new IntPtr(1);
        }
        return Natif.CallNextHookEx(IntPtr.Zero, nCode, wParam, lParam);
    }

    /// <summary>Traduit une touche physique en action sur le mot de passe, avec la disposition du clavier de la fenêtre
    /// active et les modificateurs tenus (vus par le crochet, puisque Windows ne les reçoit plus). Les caractères
    /// passent par un tampon effacé aussitôt, jamais par une chaîne .NET.</summary>
    private ActionSaisie? Traduire(Natif.KBDLLHOOKSTRUCT k)
    {
        int vk = (int)k.vkCode;
        if (Touches.EstModificateur(vk) && !Touches.EstWin(vk)) return null;
        Array.Clear(etatClavier);
        foreach (int t in suivi.Tenues) if (t is > 0 and < 256) etatClavier[t] = 0x80;
        if (suivi.Maj) etatClavier[Touches.Maj] = 0x80;
        if (suivi.Ctrl) etatClavier[Touches.Ctrl] = 0x80;
        if (suivi.Alt) etatClavier[Touches.Alt] = 0x80;
        if ((NatifGarde.GetKeyState(Touches.VerrMaj) & 1) != 0) etatClavier[Touches.VerrMaj] |= 1;
        var devant = Natif.GetForegroundWindow();
        var disposition = NatifGarde.GetKeyboardLayout(devant == IntPtr.Zero ? 0 : Natif.GetWindowThreadProcessId(devant, out _));
        derniereDisposition = disposition;
        int n = NatifGarde.ToUnicodeEx(k.vkCode, k.scanCode, etatClavier, caracteres, caracteres.Length, 0, disposition);
        try
        {
            toucheMorte = n < 0;
            return ActionSaisie.Depuis(vk, n > 0 ? caracteres.AsSpan(0, Math.Min(n, caracteres.Length)) : ReadOnlySpan<char>.Empty,
                suivi.Ctrl, suivi.Alt, suivi.Win);
        }
        finally { Array.Clear(caracteres); }
    }

    /// <summary>Vide l'état « touche morte » de la disposition (une espace la consomme).</summary>
    private void OublierToucheMorte()
    {
        if (!toucheMorte) return;
        toucheMorte = false;
        Array.Clear(etatClavier);
        for (int i = 0; i < 2 && NatifGarde.ToUnicodeEx(Touches.Espace, 0x39, etatClavier, caracteres, caracteres.Length, 0, derniereDisposition) < 0; i++) { }
        Array.Clear(caracteres);
    }

    /// <summary>Vrai si l'événement doit être avalé.</summary>
    private bool Appliquer(Verdict v)
    {
        switch (v.Decision)
        {
            case Decision.Laisser: return false;
            case Decision.Poser:
                if (!v.Repetition) SurPoser?.Invoke();
                return true;
            case Decision.Reveiller:
                SurReveil?.Invoke();
                return true;
            default: return true;
        }
    }

    public void Dispose()
    {
        if (fil is null) return;
        Natif.PostThreadMessage(idFil, Natif.WM_QUIT, IntPtr.Zero, IntPtr.Zero);
        fil.Join(2000);
        fil = null;
    }
}
