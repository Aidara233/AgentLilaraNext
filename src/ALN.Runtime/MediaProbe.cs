namespace ALN.Runtime;

/// <summary>
/// What a run of bytes is, read off its first few dozen bytes — and never by decoding it.
///
/// The rule this exists to keep: <b>this process has no image decoder.</b> A decoder is a parser fed by strangers, and
/// the pictures that arrive here come from a chat platform. Everything the rest of the system needs to know about a
/// picture — is it a picture, which format, how many pixels — is in its header, and reading a header is a lookup, not
/// an interpretation. When a header cannot be understood, the answer is "not known", never a guess: a token estimate
/// built on a guessed size is wrong in whichever direction the guess went.
///
/// What is recognised, and what is not:
/// <list type="bullet">
///   <item>PNG, JPEG, GIF, WebP — the four a vision model accepts nearly everywhere.</item>
///   <item><b>SVG is not recognised, on purpose.</b> It is XML: text with instructions in it, which makes it a picture
///   in one program and a document in another, and several providers refuse it outright. Storing it as
///   <see cref="ALN.Contracts.MediaKind.File"/> is the honest description.</item>
///   <item>BMP, TIFF, AVIF, HEIC: not recognised either. Each is a decoder this build does not have, and a format
///   people rarely send is not worth the surface.</item>
/// </list>
/// </summary>
public static class MediaProbe
{
    /// <summary>How much of the file the caller should offer. Enough for a JPEG's EXIF block before its frame header.</summary>
    public const int HeaderBytes = 64 * 1024;

    /// <summary>The format of these bytes, by their content. Never by a file name: an extension is a claim.</summary>
    public static ALN.Contracts.MediaInfo Detect(ReadOnlySpan<byte> header)
    {
        if (StartsWith(header, [0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A]))
        {
            var (width, height) = PngSize(header);
            return Picture("image/png", "png", width, height);
        }
        if (header.Length >= 3 && header[0] == 0xFF && header[1] == 0xD8 && header[2] == 0xFF)
        {
            var (width, height) = JpegSize(header);
            return Picture("image/jpeg", "jpg", width, height);
        }
        if (StartsWith(header, "GIF87a"u8) || StartsWith(header, "GIF89a"u8))
        {
            return Picture("image/gif", "gif",
                header.Length >= 10 ? LittleEndian16(header, 6) : null,
                header.Length >= 10 ? LittleEndian16(header, 8) : null);
        }
        if (StartsWith(header, "RIFF"u8) && header.Length >= 16 && StartsWith(header[8..], "WEBP"u8))
        {
            var (width, height) = WebpSize(header);
            return Picture("image/webp", "webp", width, height);
        }
        if (StartsWith(header, "OggS"u8))
            return new(ALN.Contracts.MediaKind.Voice, "audio/ogg", "ogg");
        if (StartsWith(header, "ID3"u8) || (header.Length >= 2 && header[0] == 0xFF && (header[1] & 0xE0) == 0xE0))
            return new(ALN.Contracts.MediaKind.Voice, "audio/mpeg", "mp3");
        if (StartsWith(header, "fLaC"u8))
            return new(ALN.Contracts.MediaKind.Voice, "audio/flac", "flac");
        if (StartsWith(header, "RIFF"u8) && header.Length >= 12 && StartsWith(header[8..], "WAVE"u8))
            return new(ALN.Contracts.MediaKind.Voice, "audio/wav", "wav");
        if (header.Length >= 12 && StartsWith(header, "ftyp"u8, offset: 4))
        {
            // ISO base media: the brand says which kind of thing it is. Anything not named here is just a file.
            var brand = System.Text.Encoding.ASCII.GetString(header.Slice(8, 4));
            if (brand is "isom" or "iso2" or "mp41" or "mp42" or "avc1" or "M4V ")
                return new(ALN.Contracts.MediaKind.Video, "video/mp4", "mp4");
        }
        if (header.Length >= 4 && header[0] == 0x1A && header[1] == 0x45 && header[2] == 0xDF && header[3] == 0xA3)
            return new(ALN.Contracts.MediaKind.Video, "video/webm", "webm");
        return ALN.Contracts.MediaInfo.Unknown;
    }

    private static ALN.Contracts.MediaInfo Picture(string mediaType, string extension, int? width, int? height) =>
        new(ALN.Contracts.MediaKind.Image, mediaType, extension, width, height);

    /// <summary>PNG: IHDR is mandated to be the first chunk, so its size is at a fixed offset.</summary>
    private static (int?, int?) PngSize(ReadOnlySpan<byte> header) =>
        header.Length >= 24 ? (BigEndian32(header, 16), BigEndian32(header, 20)) : (null, null);

    /// <summary>
    /// JPEG: walk the marker chain to the frame header.
    ///
    /// The chain is walked rather than searched because a JPEG can legally contain <c>FF C0</c> inside its EXIF block,
    /// and "first plausible match wins" is how a picture of the wrong size gets estimated. A malformed chain ends the
    /// walk with an unknown size, which is the correct answer for a file we cannot describe.
    /// </summary>
    private static (int?, int?) JpegSize(ReadOnlySpan<byte> header)
    {
        var position = 2;
        while (position + 4 <= header.Length)
        {
            if (header[position] != 0xFF) { position++; continue; }
            var marker = header[position + 1];
            // Filler and standalone markers carry no length.
            if (marker is 0xFF or 0x01 or (>= 0xD0 and <= 0xD7) or 0xD8) { position += 2; continue; }
            if (marker == 0xD9) return (null, null);              // end of image before any frame header
            if (position + 4 > header.Length) break;
            var length = BigEndian16(header, position + 2);
            if (length < 2) return (null, null);
            if (marker is >= 0xC0 and <= 0xCF and not 0xC4 and not 0xC8 and not 0xCC)
            {
                // SOFn: length(2) precision(1) height(2) width(2) …
                return position + 9 <= header.Length
                    ? (BigEndian16(header, position + 7), BigEndian16(header, position + 5))
                    : (null, null);
            }
            position += 2 + length;
        }
        return (null, null);
    }

    /// <summary>
    /// WebP: three container flavours, each putting the canvas size somewhere different.
    /// <list type="bullet">
    ///   <item><c>VP8X</c> — extended format: 24-bit canvas size, minus one.</item>
    ///   <item><c>VP8 </c> — lossy bitstream: a 3-byte frame tag, a start code, then two 14-bit values.</item>
    ///   <item><c>VP8L</c> — lossless: one signature byte, then width-1 and height-1 in 14 bits each.</item>
    /// </list>
    /// </summary>
    private static (int?, int?) WebpSize(ReadOnlySpan<byte> header)
    {
        if (header.Length < 16) return (null, null);
        var format = System.Text.Encoding.ASCII.GetString(header.Slice(12, 4));
        if (format == "VP8X")
            return header.Length >= 30 ? (LittleEndian24(header, 24) + 1, LittleEndian24(header, 27) + 1) : (null, null);
        if (format == "VP8 ")
        {
            if (header.Length < 30 || header[23] != 0x9D || header[24] != 0x01 || header[25] != 0x2A) return (null, null);
            return (LittleEndian16(header, 26) & 0x3FFF, LittleEndian16(header, 28) & 0x3FFF);
        }
        if (format == "VP8L")
        {
            if (header.Length < 25 || header[20] != 0x2F) return (null, null);
            var packed = (uint)(header[21] | (header[22] << 8) | (header[23] << 16) | (header[24] << 24));
            return ((int)(packed & 0x3FFF) + 1, (int)((packed >> 14) & 0x3FFF) + 1);
        }
        return (null, null);
    }

    private static bool StartsWith(ReadOnlySpan<byte> data, ReadOnlySpan<byte> prefix, int offset = 0) =>
        data.Length >= offset + prefix.Length && data.Slice(offset, prefix.Length).SequenceEqual(prefix);

    private static int BigEndian32(ReadOnlySpan<byte> data, int offset) =>
        (data[offset] << 24) | (data[offset + 1] << 16) | (data[offset + 2] << 8) | data[offset + 3];

    private static int BigEndian16(ReadOnlySpan<byte> data, int offset) => (data[offset] << 8) | data[offset + 1];

    private static int LittleEndian16(ReadOnlySpan<byte> data, int offset) => data[offset] | (data[offset + 1] << 8);

    private static int LittleEndian24(ReadOnlySpan<byte> data, int offset) =>
        data[offset] | (data[offset + 1] << 8) | (data[offset + 2] << 16);
}
