using System;
using System.IO;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace FaceLockApp.Services;

public class SecurityService
{
    private static readonly string AppFolder = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "FaceLockApp"
    );

    private static readonly string PasswordFilePath = Path.Combine(AppFolder, "auth.dat");
    private static readonly string GalleryFilePath = Path.Combine(AppFolder, "owner_gallery.dat");

    public SecurityService()
    {
        if (!Directory.Exists(AppFolder))
        {
            Directory.CreateDirectory(AppFolder);
        }
    }

    public bool IsPasswordConfigured()
    {
        return File.Exists(PasswordFilePath);
    }

    public void SetPassword(string newPassword)
    {
        if (string.IsNullOrWhiteSpace(newPassword))
            throw new ArgumentException("Password cannot be empty.");

        byte[] salt = RandomNumberGenerator.GetBytes(16);
        byte[] hash = Rfc2898DeriveBytes.Pbkdf2(
            Encoding.UTF8.GetBytes(newPassword),
            salt,
            iterations: 100_000,
            hashAlgorithm: HashAlgorithmName.SHA256,
            outputLength: 32
        );

        var record = new AuthRecord
        {
            SaltBase64 = Convert.ToBase64String(salt),
            HashBase64 = Convert.ToBase64String(hash)
        };

        string json = JsonSerializer.Serialize(record);
        byte[] rawBytes = Encoding.UTF8.GetBytes(json);
        byte[] encrypted = ProtectedData.Protect(rawBytes, null, DataProtectionScope.CurrentUser);

        File.WriteAllBytes(PasswordFilePath, encrypted);
    }

    public bool ValidatePassword(string password)
    {
        if (!IsPasswordConfigured() || string.IsNullOrEmpty(password))
            return false;

        try
        {
            byte[] encrypted = File.ReadAllBytes(PasswordFilePath);
            byte[] decrypted = ProtectedData.Unprotect(encrypted, null, DataProtectionScope.CurrentUser);
            string json = Encoding.UTF8.GetString(decrypted);

            var record = JsonSerializer.Deserialize<AuthRecord>(json);
            if (record == null) return false;

            byte[] salt = Convert.FromBase64String(record.SaltBase64);
            byte[] expectedHash = Convert.FromBase64String(record.HashBase64);

            byte[] computedHash = Rfc2898DeriveBytes.Pbkdf2(
                Encoding.UTF8.GetBytes(password),
                salt,
                iterations: 100_000,
                hashAlgorithm: HashAlgorithmName.SHA256,
                outputLength: 32
            );

            return CryptographicOperations.FixedTimeEquals(expectedHash, computedHash);
        }
        catch
        {
            return false;
        }
    }

    public OwnerGallery LoadGallery()
    {
        if (!File.Exists(GalleryFilePath))
        {
            return new OwnerGallery();
        }

        try
        {
            byte[] encrypted = File.ReadAllBytes(GalleryFilePath);
            byte[] decrypted = ProtectedData.Unprotect(encrypted, null, DataProtectionScope.CurrentUser);
            string json = Encoding.UTF8.GetString(decrypted);
            return JsonSerializer.Deserialize<OwnerGallery>(json) ?? new OwnerGallery();
        }
        catch
        {
            return new OwnerGallery();
        }
    }

    public void SaveGallery(OwnerGallery gallery)
    {
        string json = JsonSerializer.Serialize(gallery);
        byte[] rawBytes = Encoding.UTF8.GetBytes(json);
        byte[] encrypted = ProtectedData.Protect(rawBytes, null, DataProtectionScope.CurrentUser);
        File.WriteAllBytes(GalleryFilePath, encrypted);
    }

    public void AddSample(FaceSample sample)
    {
        var gallery = LoadGallery();
        gallery.Samples.Add(sample);
        SaveGallery(gallery);
    }

    public bool DeleteSample(string sampleId)
    {
        var gallery = LoadGallery();
        int removed = gallery.Samples.RemoveAll(s => s.Id == sampleId);
        if (removed > 0)
        {
            SaveGallery(gallery);
            return true;
        }
        return false;
    }

    private class AuthRecord
    {
        public string SaltBase64 { get; set; } = string.Empty;
        public string HashBase64 { get; set; } = string.Empty;
    }
}
