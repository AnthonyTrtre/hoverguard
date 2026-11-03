using System;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using Hoverguard.Coeur;
using Windows.Devices.Enumeration;
using Windows.Media.Capture;
using Windows.Media.MediaProperties;
using Windows.Storage;

namespace Hoverguard;

/// <summary>Une photo par la caméra intégrée (option, désactivée par défaut), gardée en local dans
/// <c>%LOCALAPPDATA%\Utahpik\Hoverguard\intrus</c> et effacée au bout de 30 jours. Windows applique son réglage
/// « Autoriser les applications de bureau à accéder à votre caméra » : s'il est coupé, il n'y a simplement pas de
/// photo.</summary>
internal static class Camera
{
    /// <summary>La caméra intégrée seulement (écran du portable, tablette) : jamais une webcam USB, une caméra
    /// virtuelle ou un téléphone, comme sur macOS. Windows indique un emplacement (avant, arrière…) pour les caméras
    /// intégrées au boîtier ; les autres n'en ont pas.</summary>
    private static async Task<DeviceInformation?> CameraIntegree()
    {
        var cameras = await DeviceInformation.FindAllAsync(DeviceClass.VideoCapture);
        return cameras.Where(c => c.IsEnabled && c.EnclosureLocation is { Panel: not Panel.Unknown })
                      .OrderBy(c => c.EnclosureLocation.Panel == Panel.Front ? 0 : 1)  // face à l'utilisateur d'abord
                      .FirstOrDefault();
    }

    public static async Task<string?> Photographier()
    {
        try
        {
            if (await CameraIntegree() is not DeviceInformation camera)
            {
                Journal.Info("Pas de photo : aucune caméra intégrée");
                return null;
            }
            Directory.CreateDirectory(Dossiers.Intrus);
            using var capture = new MediaCapture();
            await capture.InitializeAsync(new MediaCaptureInitializationSettings
            {
                VideoDeviceId = camera.Id,
                StreamingCaptureMode = StreamingCaptureMode.Video,
                SharingMode = MediaCaptureSharingMode.ExclusiveControl,
            });
            await Task.Delay(1200);  // le temps que l'exposition se règle
            var dossier = await StorageFolder.GetFolderFromPathAsync(Dossiers.Intrus);
            var fichier = await dossier.CreateFileAsync($"intrus-{DateTimeOffset.UtcNow.ToUnixTimeSeconds()}.jpg", CreationCollisionOption.GenerateUniqueName);
            await capture.CapturePhotoToStorageFileAsync(ImageEncodingProperties.CreateJpeg(), fichier);
            return fichier.Path;
        }
        catch (Exception e)
        {
            Journal.Erreur($"Photo impossible : {e.Message}");
            return null;
        }
    }

    /// <summary>Efface les photos de plus de 30 jours (audit M8). Appelé au démarrage de Hoverguard.</summary>
    public static void Purger()
    {
        try
        {
            if (!Directory.Exists(Dossiers.Intrus)) return;
            var fichiers = new DirectoryInfo(Dossiers.Intrus).EnumerateFiles()
                .Select(f => (f.FullName, (DateTime?)f.CreationTimeUtc));
            foreach (var chemin in PhotosIntrus.AEffacer(fichiers, DateTime.UtcNow))
            {
                try { File.Delete(chemin); }
                catch (Exception e) when (e is IOException or UnauthorizedAccessException) { Journal.Erreur($"Photo non effacée : {e.Message}"); }
            }
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException) { Journal.Erreur($"Purge des photos : {e.Message}"); }
    }
}
