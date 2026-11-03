using System.Runtime.InteropServices;
using System.Security;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace Hoverguard.Coeur;

/// <summary>
/// Le mot de passe propre à Hoverguard, choisi par l'utilisateur au premier lancement : c'est lui (et lui seul) qui
/// lève la garde sous Windows. Il n'est jamais stocké : seule une empreinte PBKDF2-SHA256 salée (600 000 itérations)
/// est écrite dans <c>%APPDATA%\Utahpik\Hoverguard\motdepasse.json</c>. Le texte tapé n'existe en clair que le temps
/// du calcul, dans des tampons effacés ensuite ; il n'est jamais converti en chaîne .NET.
/// </summary>
public sealed class MotDePasseHoverguard
{
    public const int LongueurMinimale = 4;
    private const int Iterations = 600_000;

    [JsonPropertyName("version")] public int Version { get; init; } = 1;
    [JsonPropertyName("iterations")] public int NombreIterations { get; init; } = Iterations;
    [JsonPropertyName("sel")] public string Sel { get; init; } = "";
    [JsonPropertyName("empreinte")] public string Empreinte { get; init; } = "";

    /// <summary>Nouvelle empreinte pour ce mot de passe (sel aléatoire de 16 octets).</summary>
    public static MotDePasseHoverguard Creer(SecureString motDePasse)
    {
        if (motDePasse.Length < LongueurMinimale) throw new ArgumentException($"{LongueurMinimale} caractères au moins");
        var sel = RandomNumberGenerator.GetBytes(16);
        return new MotDePasseHoverguard
        {
            Sel = Convert.ToBase64String(sel),
            Empreinte = Convert.ToBase64String(Deriver(motDePasse, sel, Iterations)),
        };
    }

    public bool Verifier(SecureString motDePasse)
    {
        if (motDePasse.Length == 0 || NombreIterations < 100_000) return false;
        try
        {
            var attendue = Convert.FromBase64String(Empreinte);
            var calculee = Deriver(motDePasse, Convert.FromBase64String(Sel), NombreIterations);
            return CryptographicOperations.FixedTimeEquals(attendue, calculee);
        }
        catch (FormatException) { return false; }
    }

    private static byte[] Deriver(SecureString motDePasse, byte[] sel, int iterations)
    {
        IntPtr clair = IntPtr.Zero;
        var caracteres = new char[motDePasse.Length];
        byte[]? octets = null;
        try
        {
            clair = Marshal.SecureStringToGlobalAllocUnicode(motDePasse);
            Marshal.Copy(clair, caracteres, 0, caracteres.Length);
            octets = new byte[Encoding.UTF8.GetMaxByteCount(caracteres.Length)];
            int n = Encoding.UTF8.GetBytes(caracteres, octets);
            return Rfc2898DeriveBytes.Pbkdf2(octets.AsSpan(0, n), sel, iterations, HashAlgorithmName.SHA256, 32);
        }
        finally
        {
            Array.Clear(caracteres);
            if (octets is not null) CryptographicOperations.ZeroMemory(octets);
            if (clair != IntPtr.Zero) Marshal.ZeroFreeGlobalAllocUnicode(clair);
        }
    }

    /// <summary>Lit l'empreinte ; absente ou abîmée : null (Hoverguard redemande un mot de passe).</summary>
    public static MotDePasseHoverguard? Charger(string chemin)
    {
        try
        {
            if (!File.Exists(chemin)) return null;
            var m = JsonSerializer.Deserialize<MotDePasseHoverguard>(File.ReadAllText(chemin));
            return m is { Sel.Length: > 0, Empreinte.Length: > 0 } ? m : null;
        }
        catch (Exception e) when (e is JsonException or IOException or UnauthorizedAccessException) { return null; }
    }

    public void Enregistrer(string chemin)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(chemin)!);
        var temporaire = chemin + ".tmp";
        File.WriteAllText(temporaire, JsonSerializer.Serialize(this, new JsonSerializerOptions { WriteIndented = true }), new UTF8Encoding(false));
        File.Move(temporaire, chemin, overwrite: true);
    }
}
