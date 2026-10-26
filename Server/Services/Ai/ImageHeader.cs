namespace Server.Services.Ai;

/// <summary>
/// Dimensiones de una imagen leidas de su cabecera, sin decodificarla. Lo usan
/// los providers que tienen que elegir un tamano de salida parecido al de la
/// imagen de entrada (gpt-image al editar, los modelos de video al animar).
/// </summary>
public static class ImageHeader
{
    /// <summary>
    /// Ancho y alto de una imagen PNG, JPEG o WebP. (0, 0) si no se reconoce el formato.
    /// </summary>
    public static (int Width, int Height) ReadDimensions(byte[] data)
    {
        if (data.Length < 24) return (0, 0);

        // PNG: bytes 0-7 = signature, IHDR chunk at byte 16: width(4) height(4)
        if (data[0] == 0x89 && data[1] == 0x50 && data[2] == 0x4E && data[3] == 0x47)
        {
            var w = (data[16] << 24) | (data[17] << 16) | (data[18] << 8) | data[19];
            var h = (data[20] << 24) | (data[21] << 16) | (data[22] << 8) | data[23];
            return (w, h);
        }

        // JPEG: scan for SOF0 (0xFF 0xC0) or SOF2 (0xFF 0xC2) marker
        if (data[0] == 0xFF && data[1] == 0xD8)
        {
            int i = 2;
            while (i + 9 < data.Length)
            {
                if (data[i] != 0xFF) { i++; continue; }
                var marker = data[i + 1];
                if (marker == 0xC0 || marker == 0xC2)
                {
                    var h = (data[i + 5] << 8) | data[i + 6];
                    var w = (data[i + 7] << 8) | data[i + 8];
                    return (w, h);
                }
                // Skip to next marker
                var len = (data[i + 2] << 8) | data[i + 3];
                i += 2 + len;
            }
        }

        // WebP: RIFF header, 'WEBP' at byte 8, VP8 at byte 12
        if (data.Length > 30 && data[0] == 0x52 && data[1] == 0x49 && data[2] == 0x46 && data[3] == 0x46
            && data[8] == 0x57 && data[9] == 0x45 && data[10] == 0x42 && data[11] == 0x50)
        {
            // VP8L (lossless)
            if (data[12] == 0x56 && data[13] == 0x50 && data[14] == 0x38 && data[15] == 0x4C && data.Length > 25)
            {
                var bits = (uint)data[21] | ((uint)data[22] << 8) | ((uint)data[23] << 16) | ((uint)data[24] << 24);
                var w = (int)(bits & 0x3FFF) + 1;
                var h = (int)((bits >> 14) & 0x3FFF) + 1;
                return (w, h);
            }
            // VP8 (lossy) — frame header at byte 23+
            if (data[12] == 0x56 && data[13] == 0x50 && data[14] == 0x38 && data[15] == 0x20 && data.Length > 29)
            {
                var w = (data[26] | (data[27] << 8)) & 0x3FFF;
                var h = (data[28] | (data[29] << 8)) & 0x3FFF;
                return (w, h);
            }
        }

        return (0, 0);
    }
}
