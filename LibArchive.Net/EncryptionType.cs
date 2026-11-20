namespace LibArchive.Net;

/// <summary>
/// Specifies the encryption algorithm for password-protected archives.
/// libarchive can only encrypt ZIP archives: a password combined with any other archive format is rejected
/// with <see cref="System.NotSupportedException"/>, unless the type is <see cref="None"/>.
/// </summary>
public enum EncryptionType
{
    /// <summary>
    /// Use the default encryption, which is AES-256 (ZIP only).
    /// </summary>
    Default,

    /// <summary>
    /// No encryption (even if password is provided): the password is ignored and the archive is written
    /// unencrypted. Valid for every archive format.
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
    /// AES-256 encryption (ZIP only: libarchive cannot encrypt 7-Zip archives).
    /// Recommended for strong security.
    /// </summary>
    AES256,

    /// <summary>
    /// Alias for <see cref="Traditional"/> - legacy PKWARE encryption.
    /// </summary>
    ZipCrypto = Traditional,
}
