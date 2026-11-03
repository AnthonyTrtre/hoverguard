using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;
using System.Threading;
using Hoverguard.Coeur;

namespace Hoverguard;

/// <summary>Relève les processus de Windows pour la détection automatique des agents (<see cref="ReglesDetection"/>).
/// Appelé hors du fil de l'interface, toutes les 10 s. Choix pour rester léger :
/// <list type="bullet">
/// <item>liste et parents par un instantané Toolhelp32 (un seul appel système, sans WMI) ;</item>
/// <item>temps processeur et date de création par <c>GetProcessTimes</c>, avec le droit minimal
///   <c>PROCESS_QUERY_LIMITED_INFORMATION</c> (les processus protégés refusés sont simplement ignorés) ;</item>
/// <item>chemin complet (<c>QueryFullProcessImageNameW</c>) seulement pour les exécutables d'agents et les interprètes, et
///   ligne de commande (<c>NtQueryInformationProcess</c>, classe 60, Windows 8.1+) seulement pour les interprètes :
///   WMI <c>Win32_Process</c> coûterait des centaines de millisecondes et un service de plus à chaque relevé.</item>
/// </list>
/// Rien ne sort du PC, rien n'est journalisé des lignes de commande.</summary>
internal sealed class Detecteur
{
    private readonly MesureActivite mesure = new();
    private readonly object verrou = new();

    public IReadOnlyList<AgentDetecte> Relever()
    {
        lock (verrou)
        {
            var processus = new List<ProcessusVu>(512);
            foreach (var (pid, parent, exe) in Instantane())
            {
                long creation = 0;
                TimeSpan cpu = TimeSpan.Zero;
                string? agent = null;
                var h = OpenProcess(PROCESS_QUERY_LIMITED_INFORMATION, false, (uint)pid);
                if (h != IntPtr.Zero)
                {
                    try
                    {
                        if (GetProcessTimes(h, out var c, out _, out var noyau, out var utilisateur))
                        {
                            creation = c;
                            cpu = TimeSpan.FromTicks(noyau + utilisateur);  // unités de 100 ns, comme les ticks .NET
                        }
                        if (ReglesDetection.MeriteExamen(exe))
                        {
                            var chemin = Chemin(h) ?? exe;
                            var arguments = ReglesDetection.EstInterprete(exe) ? ReglesDetection.Decouper(LigneDeCommande(h)) : [];
                            agent = ReglesDetection.Agent(chemin, arguments);
                        }
                    }
                    finally { CloseHandle(h); }
                }
                else if (ReglesDetection.MeriteExamen(exe) && !ReglesDetection.EstInterprete(exe))
                {
                    // Processus inaccessible (autre session, protégé) : reconnu par son seul nom, sans mesure d'activité.
                    agent = ReglesDetection.Agent(exe, []);
                }
                processus.Add(new ProcessusVu(pid, parent, creation, cpu, agent));
            }
            return mesure.Relever(processus, DateTime.UtcNow);
        }
    }

    private static List<(int Pid, int Parent, string Exe)> Instantane()
    {
        var liste = new List<(int, int, string)>(512);
        var instantane = CreateToolhelp32Snapshot(TH32CS_SNAPPROCESS, 0);
        if (instantane == INVALID_HANDLE_VALUE) return liste;
        try
        {
            var e = new PROCESSENTRY32W { dwSize = (uint)Marshal.SizeOf<PROCESSENTRY32W>() };
            if (!Process32FirstW(instantane, ref e)) return liste;
            do
            {
                if (e.th32ProcessID > 4) liste.Add(((int)e.th32ProcessID, (int)e.th32ParentProcessID, e.szExeFile ?? ""));
                e.dwSize = (uint)Marshal.SizeOf<PROCESSENTRY32W>();
            } while (Process32NextW(instantane, ref e));
        }
        finally { CloseHandle(instantane); }
        return liste;
    }

    private static string? Chemin(IntPtr h)
    {
        var tampon = new char[1024];
        int taille = tampon.Length;
        return QueryFullProcessImageNameW(h, 0, tampon, ref taille) && taille > 0 ? new string(tampon, 0, taille) : null;
    }

    /// <summary>Ligne de commande d'un autre processus : la structure renvoyée est un UNICODE_STRING suivi du texte.</summary>
    private static string? LigneDeCommande(IntPtr h)
    {
        int taille = 0;
        int statut = NtQueryInformationProcess(h, ProcessCommandLineInformation, IntPtr.Zero, 0, ref taille);
        if (statut != STATUS_INFO_LENGTH_MISMATCH || taille <= 0 || taille > 1 << 20) return null;
        var tampon = Marshal.AllocHGlobal(taille);
        try
        {
            if (NtQueryInformationProcess(h, ProcessCommandLineInformation, tampon, taille, ref taille) != 0) return null;
            var u = Marshal.PtrToStructure<UNICODE_STRING>(tampon);
            return u.Buffer == IntPtr.Zero || u.Length == 0 ? null : Marshal.PtrToStringUni(u.Buffer, u.Length / 2);
        }
        finally { Marshal.FreeHGlobal(tampon); }
    }

    // ── Win32 ───────────────────────────────────────────────────────────────────────────────────────────────────────
    private const uint TH32CS_SNAPPROCESS = 0x2, PROCESS_QUERY_LIMITED_INFORMATION = 0x1000;
    private const int ProcessCommandLineInformation = 60;
    private const int STATUS_INFO_LENGTH_MISMATCH = unchecked((int)0xC0000004);
    private static readonly IntPtr INVALID_HANDLE_VALUE = new(-1);

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct PROCESSENTRY32W
    {
        public uint dwSize, cntUsage, th32ProcessID;
        public IntPtr th32DefaultHeapID;
        public uint th32ModuleID, cntThreads, th32ParentProcessID;
        public int pcPriClassBase;
        public uint dwFlags;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 260)] public string szExeFile;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct UNICODE_STRING { public ushort Length, MaximumLength; public IntPtr Buffer; }

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern IntPtr CreateToolhelp32Snapshot(uint dwFlags, uint th32ProcessID);
    [DllImport("kernel32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    private static extern bool Process32FirstW(IntPtr hSnapshot, ref PROCESSENTRY32W lppe);
    [DllImport("kernel32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    private static extern bool Process32NextW(IntPtr hSnapshot, ref PROCESSENTRY32W lppe);
    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern IntPtr OpenProcess(uint dwDesiredAccess, bool bInheritHandle, uint dwProcessId);
    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool CloseHandle(IntPtr hObject);
    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool GetProcessTimes(IntPtr hProcess, out long creation, out long exit, out long kernel, out long user);
    [DllImport("kernel32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    private static extern bool QueryFullProcessImageNameW(IntPtr hProcess, uint dwFlags, [Out] char[] lpExeName, ref int lpdwSize);
    [DllImport("ntdll.dll")]
    private static extern int NtQueryInformationProcess(IntPtr hProcess, int infoClass, IntPtr info, int length, ref int returnLength);
}

/// <summary>Lance un relevé en arrière-plan sans jamais en empiler deux.</summary>
internal static class RelevesDetection
{
    private static int enCours;

    public static void Lancer(Detecteur detecteur, Action<IReadOnlyList<AgentDetecte>, ReleveJournaux> surResultat)
    {
        if (Interlocked.Exchange(ref enCours, 1) == 1) return;
        ThreadPool.QueueUserWorkItem(_ =>
        {
            try { surResultat(detecteur.Relever(), JournauxAgents.Relever(Dossiers.Utilisateur, DateTime.UtcNow)); }
            catch (Exception e) { Journal.Erreur($"Détection des agents : {e.Message}"); }
            finally { Volatile.Write(ref enCours, 0); }
        });
    }
}
