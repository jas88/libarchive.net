namespace LibArchive.Net;

/// <summary>
/// Specifies the encryption algorithm for password-protected archives.
/// </summary>
public enum EncryptionType
{
    /// <summary>
    /// Use the default encryption for the archive format.
    /// ZIP: AES-256, 7-Zip: AES-256
    /// </summary>
    Default,

    /// <summary>
    /// No encryption (even if password is provided).
    /// </summary>
    None,

    /// <summary>
    /// Traditional PKWARE encryption for ZIP archives (weak, legacy compatibility only).
    /// Also known as ZipCrypto.
    /// </summary>
    Traditional,

    /// <summary>
    /// AES-128 encryption (ZIP only).
    /// </summary>
    AES128,

    /// <summary>
    /// AES-192 encryption (ZIP only).
    /// </summary>
    AES192,

    /// <summary>
    /// AES-256 encryption (ZIP and 7-Zip).
    /// Recommended for strong security.
    /// </summary>
    AES256,

    /// <summary>
    /// Alias for Traditional - legacy PKWARE encryption.
    /// </summary>
    ZipCrypto,
}
