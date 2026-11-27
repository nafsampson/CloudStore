//summary//
// Applies bounded reads, allowlisted types and generated names before any storage write.
//summary//
//-------------------Start Of File------------------//
using System.Buffers.Binary;
using System.Text;
using System.Text.RegularExpressions;

namespace CloudStore.Uploads;

public sealed class UploadRejected(string message) : Exception(message);
public sealed record ValidatedUpload(string Name, string ContentType, byte[] Bytes);
public static class UploadPolicy
{
    public const int ImageLimit = 4 * 1024 * 1024;
    public const int ContractLimit = 4 * 1024 * 1024;
    public const int JsonLimit = 6 * 1024 * 1024;

    public static bool StoredName(string? name, bool image) => name is not null && Regex.IsMatch(name,
        image ? @"\A[a-f0-9]{32}\.(png|jpg)\z" : @"\A[a-f0-9]{32}\.(pdf|txt)\z", RegexOptions.CultureInvariant);

    public static async Task<byte[]> ReadAsync(Stream source, int limit, CancellationToken cancellationToken = default)
    {
        using var output = new MemoryStream();
        var buffer = new byte[16384];
        while (true)
        {
            var read = await source.ReadAsync(buffer.AsMemory(0, Math.Min(buffer.Length, limit + 1 - (int)output.Length)), cancellationToken);
            if (read == 0) break;
            output.Write(buffer, 0, read);
            if (output.Length > limit) throw new UploadRejected("The file is too large.");
        }
        if (output.Length == 0) throw new UploadRejected("Select a non-empty file.");
        return output.ToArray();
    }

    public static ValidatedUpload Validate(string? filename, string? declaredType, byte[] bytes, bool image)
    {
        // Reject both Windows and Unix path syntax, controls and encoded-path tricks.
        if (string.IsNullOrWhiteSpace(filename) || filename.Length > 150 ||
            filename.Any(c => char.IsControl(c) || c is '/' or '\\' or ':' or '%' ) || filename.Contains(".."))
            throw new UploadRejected("Use a simple filename without folders or special path characters.");
        if (bytes.Length == 0 || bytes.Length > (image ? ImageLimit : ContractLimit))
            throw new UploadRejected("The file is empty or exceeds the upload limit.");
        var extension = Path.GetExtension(filename).ToLowerInvariant();
        if (extension == ".jpeg") extension = ".jpg";
        var contentType = extension switch
        {
            ".png" when image => "image/png",
            ".jpg" when image => "image/jpeg",
            ".pdf" when !image => "application/pdf",
            ".txt" when !image => "text/plain",
            _ => throw new UploadRejected(image ? "Product images must be PNG or JPEG." : "Contracts must be PDF or UTF-8 text.")
        };
        var type = declaredType?.Split(';')[0].Trim().ToLowerInvariant();
        if (!string.IsNullOrEmpty(type) && type != "application/octet-stream" && type != contentType)
            throw new UploadRejected("The declared file type does not match its extension.");

        var span = bytes.AsSpan();
        var valid = extension switch
        {
            ".png" => Png(span),
            ".jpg" => Jpeg(span),
            ".pdf" => span.StartsWith("%PDF-"u8) && span.Length >= 12 &&
                Encoding.ASCII.GetString(span[^Math.Min(1024, span.Length)..]).TrimEnd().EndsWith("%%EOF", StringComparison.Ordinal),
            ".txt" => Text(bytes),
            _ => false
        };
        if (!valid) throw new UploadRejected("The file content does not match a supported format.");
        return new ValidatedUpload(Guid.NewGuid().ToString("N") + extension, contentType, bytes);
    }

    private static bool Dimensions(uint width, uint height) => width is > 0 and <= 4096 && height is > 0 and <= 4096 && (ulong)width * height <= 16_000_000;
    private static bool Png(ReadOnlySpan<byte> bytes) => bytes.Length >= 45 &&
        bytes.StartsWith(new byte[] {137,80,78,71,13,10,26,10}) &&
        BinaryPrimitives.ReadUInt32BigEndian(bytes[8..]) == 13 && bytes[12..16].SequenceEqual("IHDR"u8) &&
        Dimensions(BinaryPrimitives.ReadUInt32BigEndian(bytes[16..]), BinaryPrimitives.ReadUInt32BigEndian(bytes[20..])) &&
        bytes[^12..].SequenceEqual(new byte[] {0,0,0,0,73,69,78,68,174,66,96,130});

    private static bool Jpeg(ReadOnlySpan<byte> bytes)
    {
        if (bytes.Length < 12 || bytes[0] != 255 || bytes[1] != 216 || bytes[^2] != 255 || bytes[^1] != 217) return false;
        var position = 2;
        while (position + 3 < bytes.Length)
        {
            if (bytes[position++] != 255) return false;
            while (position < bytes.Length && bytes[position] == 255) position++;
            if (position >= bytes.Length) return false;
            var marker = bytes[position++];
            if (marker is 0xDA or 0xD9) return false;
            if (position + 2 > bytes.Length) return false;
            var length = BinaryPrimitives.ReadUInt16BigEndian(bytes[position..]);
            if (length < 2 || position + length > bytes.Length) return false;
            if (marker is 0xC0 or 0xC1 or 0xC2)
                return length >= 8 && Dimensions(BinaryPrimitives.ReadUInt16BigEndian(bytes[(position + 5)..]),
                    BinaryPrimitives.ReadUInt16BigEndian(bytes[(position + 3)..]));
            position += length;
        }
        return false;
    }

    private static bool Text(byte[] bytes)
    {
        try { return !new UTF8Encoding(false, true).GetString(bytes).Any(c => char.IsControl(c) && c is not ('\r' or '\n' or '\t')); }
        catch (DecoderFallbackException) { return false; }
    }
}
//-------------------End Of File------------------//
