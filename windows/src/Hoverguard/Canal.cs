using System;
using System.IO;
using System.IO.Pipes;
using System.Security.AccessControl;
using System.Security.Principal;
using System.Text;
using System.Threading;

namespace Hoverguard;

/// <summary>
/// Une seule instance de Hoverguard par session. Quand Windows ouvre une URL <c>hoverguard://…</c> (hook d'un agent),
/// il lance un second exemplaire de l'exe : celui-ci passe l'URL à l'instance en cours par un tube nommé local, puis
/// s'arrête. Le tube n'accepte que l'utilisateur courant et refuse explicitement les connexions réseau (SMB).
/// Il ne transporte que des URL, lues par <see cref="Hoverguard.Coeur.Ordre.Lire"/> : rien ne peut lever la garde.
/// </summary>
internal sealed class Canal : IDisposable
{
    private static readonly SecurityIdentifier Moi = WindowsIdentity.GetCurrent().User!;
    private static string Suffixe => $"{Moi.Value}-{System.Diagnostics.Process.GetCurrentProcess().SessionId}";
    private static string NomTube => "Hoverguard-" + Suffixe;

    private readonly Mutex mutex;
    private readonly CancellationTokenSource arret = new();
    public bool EstPremier { get; }

    public Canal()
    {
        mutex = new Mutex(true, @"Local\Hoverguard-" + Suffixe, out bool cree);
        EstPremier = cree;
    }

    /// <summary>Envoie une URL à l'instance en cours. Faux si personne n'écoute.</summary>
    public static bool Transmettre(string url)
    {
        try
        {
            using var client = new NamedPipeClientStream(".", NomTube, PipeDirection.Out, PipeOptions.CurrentUserOnly);
            client.Connect(3000);
            var octets = Encoding.UTF8.GetBytes(url.Length > 2048 ? url[..2048] : url);
            client.Write(octets);
            return true;
        }
        catch (Exception e) when (e is IOException or TimeoutException or UnauthorizedAccessException)
        {
            return false;
        }
    }

    /// <summary>Écoute sur un fil dédié ; <paramref name="recu"/> est appelé sur ce fil.</summary>
    public void Ecouter(Action<string> recu)
    {
        var fil = new Thread(() =>
        {
            var securite = new PipeSecurity();
            securite.AddAccessRule(new PipeAccessRule(Moi, PipeAccessRights.ReadWrite | PipeAccessRights.CreateNewInstance, AccessControlType.Allow));
            securite.AddAccessRule(new PipeAccessRule(new SecurityIdentifier(WellKnownSidType.NetworkSid, null), PipeAccessRights.FullControl, AccessControlType.Deny));
            while (!arret.IsCancellationRequested)
            {
                try
                {
                    using var serveur = NamedPipeServerStreamAcl.Create(NomTube, PipeDirection.In, NamedPipeServerStream.MaxAllowedServerInstances,
                        PipeTransmissionMode.Byte, PipeOptions.Asynchronous, 4096, 4096, securite);
                    serveur.WaitForConnectionAsync(arret.Token).GetAwaiter().GetResult();
                    var tampon = new byte[2048];
                    int lus = 0, n;
                    using var delai = CancellationTokenSource.CreateLinkedTokenSource(arret.Token);
                    delai.CancelAfter(2000);
                    while (lus < tampon.Length && (n = serveur.ReadAsync(tampon.AsMemory(lus), delai.Token).AsTask().GetAwaiter().GetResult()) > 0) lus += n;
                    recu(Encoding.UTF8.GetString(tampon, 0, lus));
                }
                catch (OperationCanceledException) { }
                catch (Exception e) when (e is IOException or UnauthorizedAccessException)
                {
                    Journal.Erreur($"Tube : {e.Message}");
                    Thread.Sleep(500);
                }
            }
        }) { IsBackground = true, Name = "Hoverguard canal" };
        fil.Start();
    }

    public void Dispose()
    {
        arret.Cancel();
        if (EstPremier) mutex.ReleaseMutex();
        mutex.Dispose();
    }
}
